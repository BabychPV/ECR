# B14 — Інтеграція з PI AF і FLERT: склад даних для ECR

> Закриває три з чотирьох питань «⚠ Може змінитися після аналізу бекенду»
> (`architecture/docs/13-backend-assignment.md` §12) і уточнює
> [B06](B06-integration-ports.md) під реальні джерела ECR.
> Четверте (схема `calc`) закрито в [B13](B13-ecr-calculation-engine.md).
>
> Факти — [B12](B12-ecr-as-is-backend.md) §3.1, §3.5.
> UI конфігурації джерел, мапінгу, розкладу і перевірок — [B20](B20-source-configurator.md).

---

## 1. Відповідь, яка закриває питання ТЗ §12 п.13: **RTQP уже в продуктиві**

Питання «чи доступний PI SQL Data Access Server (RTQP) у контурі» стояло як ризик,
що подвоює оцінку міграції історії ([13] §12 п.2, roadmap 0.1a). **Воно закрите фактом.**

Чинна система читає PI AF **виключно** через RTQP:

```sql
-- AF_Land_ID_FileNum_Get, SQL/DB.txt:58194
SET @LinkedServer = [dbo].[ufnGetSetting]([dbo].[ufnGetSetting](N'Mode'));
SET @TSQL = N'SELECT * FROM OPENQUERY(' + @LinkedServer + ',''
        SELECT DISTINCT EFID ID
        FROM [Master].[Category].[EventFrameAttributes_V]
        WHERE Template = N''''' + @TemplateName + N'''''
          AND StartTime BETWEEN … '')';
```

В'юха оголошена в `PI AF/ECR_01_Air_PISqlClientExportedObjects.sql`:

```sql
CREATE VIEW [Master].[Category].[EventFrameAttributes_V] AS
SELECT ef.ID AS EFID, ef.Template, ef.Modified, ef.StartTime, ef.EndTime, ef.Name,
       a.ID AS ATID, a.Name AS ParameterName, CAST(a.Value AS String) AS ParameterValue,
       ac.Category
FROM [Master].[EventFrame].[EventFrame] ef
INNER JOIN [Master].[EventFrame].[Attribute] a ON a.EventFrameID = ef.ID
LEFT  JOIN [Master].[EventFrame].[AttributeCategory] ac ON ac.AttributeID = a.ID;
```

Це працює щохвилини в 63 sync-процедурах. **Наслідки:**

* транспорт `PiSqlClientDataReader` ([13] §6) — не «оптимізація за наявності», а
  **основний** шлях; Web API потрібен лише там, де треба **писати** в AF;
* оцінку міграції історії переглядати вгору **не треба**;
* ⚠ але лишається пастка §3.4 B12: **в'юхи живуть у самій AF-базі**, тому їх
  доводиться створювати на кожному середовищі окремо. У новій системі цього
  не буде: адаптер формує запит сам, без залежності від артефактів у чужій БД
  (**ER-I-01**).

**Правка до ТЗ §12:** п.13 переходить із «❓ Потребує рішення» у «✅ Вирішено» —
[B18](B18-tz-addendum-ecr.md) §14.9.

---

## 2. Склад даних, що збираються з AF (ER-I-02)

Класифікація за долею — це і є відповідь на питання [13] §12 п.3.

| Клас даних | Шаблони AF | Доля в новій системі | Коли зникає з AF |
|------------|-----------|----------------------|------------------|
| **RDS-рядки** (введення користувача) | `HSE_Land_*` (63), `HSE_Land_07*`, `HSE_Land_08*` | джерело **на перехідний період**; після переведення введення у веб-грид дані вводяться напряму в `doc.*` | після cutover розділу ([B17](B17-ecr-transition.md) §2) |
| **Excel-модулі поза RDS** | `HSE_401_Air`, `HSE_401A_Air`, `HSE_401_Air_Import`, `HSE_400_Air_Gas_Composition`, `HSE_Thermaloxidizer(+Limit_gs/_t)` | те саме, окремими шаблонами | після cutover |
| **Довідники** | `Dictionary_Land/401/Vessel`, `PermitConstant`, `HSE_Pollutant_Emission_Fees_T5` | універсальні реєстри `cfg.Registry*`/`dic.Registry*` | §5 |
| **Перміти** | `PermitDocument`, `PermitMetaData(+Area/Location)`, `PermitEmissionValue`, `PermitDischargeValue`, `PermitAccumulationValue`, `PermitWater*` | реєстр `Permit` з `Composition`/`Association`/`ValidityWindow` ([12] §4) | §5 |
| **Конфігурація розрахунку** | `Methodology(_V)`, `Formula_El(_V)`, `Constant_El(_V)`, `ConstantsCategory`, `EmissionCalculationWork_Rules(_V)` | схема `calc.*` | [B13](B13-ecr-calculation-engine.md) §7 |
| **Налаштування періоду** | `Settings_General` (`CALCULATION_START/ENDDATETIME`), `ReportCalculationSettings` | `doc.Project.ExternalSettingsJson` + `doc.PeriodPolicy` | після cutover |
| **Керування** | `Settings_RecalculateManual`, `Recalculate_Land` | UI перерахунку ([B15](B15-ecr-recalc-orchestration.md) §2.3) | після cutover |
| **Моніторинг** | `Check_Data_Consistency`, `Check_Jobs_Error`, `Check_Logs`, `Check_System_State`, `Log`, `Logger` | внутрішні алерти і health ([B15](B15-ecr-recalc-orchestration.md) §6) | після cutover |
| **Сирі виміри** | елементи `Air_Main/Area/Region/Location/Line/Unit`, `Equipment`; потоки FLERT | **лишаються назавжди** — це операційна телеметрія, ECR її не веде | ніколи |
| **Мертве** | `HSE_*_TO_REMOVE`, `Location_TO_REMOVE` (4 шаблони) | не переносяться | одразу |

> **Головний висновок:** після повного переходу з PI AF збирається **лише останній
> клас** — сирі виміри і потоки. Усе інше або переїжджає в ECR як конфігурація/дані,
> або зникає разом із Excel-введенням. Обсяг роботи адаптера після переходу
> скорочується в рази — саме тому `PiAfConnector` і був винесений за
> `IExternalDataSource`, а не вбудований у ядро.

---

## 3. Мапінг: RDS-аркуш → універсальна модель

Той самий Event Frame, який сьогодні розбирається `CASE WHEN rn = N`, лягає на
`doc.*` без залишку. Два реальні приклади.

### 3.1 `HSE_Land_Utility_9_05` — «місяці в колонках», широка

| AS-IS | TO-BE |
|-------|-------|
| Аркуш `9. Utility`, `templateRow = 241` | `cfg.SheetDef "Utility_09"` + `cfg.TableDef "Utility_9_05"` |
| EF-шаблон `HSE_Land_Utility_9_05` | `ext.LegacyTableMapping.AfEventFrameTemplate` |
| Event Frame (документ × таблиця × місяць) | `doc.TableInstance` |
| `Attribute_0010`, `Attribute_0020`, … | `doc.TableRow.RowKey` (+ `ext.LegacyRowMapping.AfAttributeName`) |
| позиція `rn = 5` у `;`-рядку | `cfg.ColumnDef "Land_DescEqp"` + `ext.LegacyColumnMapping.LegacyFieldIndex = 5` |
| `Land_Measure_OperatingTime decimal(25,16)` | `ColumnDef.DataType = Decimal`, `Precision = 28`, `Scale = 10` |
| 20 колонок `Con_*` у **кожному рядку** | таблиця `__Header` + `TableRelationDef(MetadataSource, SyncMode = Reference)` |
| `Con_File_Number` | `doc.Document.BusinessKey` (`ColumnDef.IsBusinessKey`) |
| `Con_Region`, `Con_Area`, `Con_Contractor_Or_Company` | `ColumnDef.IsScopeField` → області доступу + `doc.DocumentIndexValue` |
| `LayoutKind` | `MonthsInColumns` (сумісність) |

### 3.2 `HSE_Land_08` (Waste) — «місяці в рядках», довга

12 блоків по 33 рядки, PK з трьох колонок `(EFID, ATID, Land_WasteNo)`
(`deliverables/WaterAndWaste/_docs/06_DESIGN_waste.md`):
`LayoutKind = MonthsInRows`, `RowMode = Fixed`, `RowKey = Land_WasteNo`,
період визначається блоком (`3 + (m-1)*33` — алгоритм 10 ТЗ §7).

> Обидва варіанти вже передбачені `TableDef.LayoutKind` ([07] §2). Нічого
> нового в ядрі не потрібно — потрібен bootstrap, який заповнить `cfg.*` і
> `ext.Legacy*Mapping` (§4).

---

## 4. Що дає перехід — у цифрах

| | Сьогодні | Після |
|---|---|---|
| Таблиць під land-дані | 63 + 63 `_History` | 0 (усе в `doc.*`) |
| Sync-процедур | 63, кожна ~400 рядків, з ручним `CASE WHEN rn = N` | 1 `DataCollectionJob` + `ext.DataMappingDef` як дані |
| Тригерів | 71 | 0 (постановка задач — у застосунку) |
| RTQP-в'юх, які треба створити на кожному AF-сервері | **32** (26 ECR + 6 FLERT) | 0 (**ER-I-01**) |
| Місць правки при додаванні колонки в аркуш | ≥ 8 (в'юха, таблиця, `_History`, синк, звіт, RDL, верифікатор, генератор) | 1 `INSERT` у `cfg.ColumnDef` |
| Прихований стан синку | `WatchdogStatus_Land.LastUpdateEF` (watermark, губить дані при `DROP TABLE`) | `UQ(DataSourceId, SourcePath, Timestamp)` — ідемпотентність без стану |

### 4.1 Семантика збору: watermark → ідемпотентність (ER-I-03)

| | Чинний синк | `DataCollectionJob` |
|---|---|---|
| Що вважається новим | `ModifiedTime > LastUpdateEF` | нічого — вставляється все, `MERGE` за унікальним ключем |
| Повторний запуск | нічого не робить (watermark уже посунуто) | той самий результат (ідемпотентність) |
| Втрата стану | `DROP TABLE` → дані не повернуться ніколи | повторний прогін відновлює все |
| Зміна значення в джерелі | оновлює (гейт `ModifiedTime`) | оновлює і рахує `ItemsUpdated` |
| Видалення в джерелі | `AF_Land_DeleteObsoleteEFID_ByTemplate` | `OnMissingInSource` (`MarkOrphaned` типово) |

Watermark лишається **оптимізацією** (`RetrievedAfter` у запиті до джерела), а не
джерелом істини: його втрата коштує зайвого читання, а не даних.

---

## 5. Довідники: master — **ECR**, з фазою `Hybrid` (ER-I-04)

Це відповідь на [13] §12 п.1.

**Рішення: майстер переїжджає в ECR, поетапно, через `SourceKind`.**

| Фаза | `RegistryDef.SourceKind` | Хто редагує | Коли |
|------|--------------------------|-------------|------|
| 1. Співіснування | `External` | PI Vision (як зараз); ECR тільки читає | до cutover розділу |
| 2. Перехідна | `Hybrid` | поля з `IsExternallyManaged` — з AF; решта — в ECR | під час cutover |
| 3. Цільова | `Local` | ECR; синхронізація вимкнена | після cutover |

**Чому master має бути ECR, а не AF:**

1. Довідники потрібні **введенню** (листбокси гриду) і **звітам** — обидва
   переїжджають в ECR; тримати master у системі, яка після переходу стане лише
   джерелом телеметрії, — це залежність без вигоди.
2. Вимоги, яких AF не дає: ID замість тексту в комірці (ФВ-8.8), темпоральний
   резолвінг «станом на дату періоду», «де використовується» перед зняттям
   значення, аудит змін довідника, RBAC на реєстр.
3. Практика вже показала ціну AF-майстра: GUID-посилання в рядкових атрибутах
   (`SubstanceId`, `PermitId`) **ламаються при перенесенні між AF-серверами** —
   елементи отримують нові ID, а значення лишаються старі (кейс UATV12 2026-08-17).

**Але рішення — не наше остаточно.** Воно зачіпає роботу PI-адміністратора і
редакторів у PI Vision, тому фіксується як **рекомендація з фазуванням**, а
`SourceKind` лишається полем налаштування, а не припущенням у коді. Відкрите
питання переформульоване: не «де master», а «коли перемикаємо фазу для кожного
реєстру» ([B18](B18-tz-addendum-ecr.md) §14.9).

### 5.1 Зовнішні ідентифікатори (ER-I-05)

Усі GUID-и, що сьогодні лежать значеннями в рядкових атрибутах AF
(`SubstanceId`, `PermitId`, `Name_water_body_Id`, `Land_ObjectId`, `Configuration!J3`,
`DropdownList!AL`), стають записами `dic.RegistryExternalKey`
`(DataSourceId, ExternalId, ExternalPath, SyncStatus)`.

**Правило, яке треба зафіксувати в коді міграції:** відповідність шукається
**за бізнес-ключем (назвою/номером), а не за GUID**, бо GUID не переживає
перенесення між AF-серверами. GUID зберігається як зовнішній ключ *після*
зіставлення, для наступних синхронізацій. Незіставлені — `SyncStatus = Orphaned`
і в звіт, ніколи не «тихо не оновилося».

---

## 6. Налаштування періоду

`Settings_General\CALCULATION_STARTDATETIME/ENDDATETIME` → сьогодні дзеркаляться
в `AF_CurrentReportPeriod (StartDateTime, EndDateTime)` — один рядок, який
визначає «відкритий звітний період» для всієї системи, і на нього дивляться
і синки (`Utility_GetDefaultStartEndDates`), і тригери, і PI Vision (кнопка `Edit`
блокується, якщо період закрито).

**TO-BE:** це `doc.Project.PeriodStart/PeriodEnd` + `doc.PeriodPolicy` з offsets
(ФВ-1.6). Значення з AF потрапляє в `Project.ExternalSettingsJson` при створенні
проєкту — **як початкове значення, не як постійна залежність** (ФВ-1.11: правило
універсальне і про AF не знає).

⚠ Тут є зміна поведінки, яку треба назвати вголос: сьогодні «відкритий період»
**один на всю систему**; у новій моделі він **на проєкт і на період**, з
grace-вікнами і ролевими надбавками. Це розширення, а не еквівалент —
див. критерій приймання в [B18](B18-tz-addendum-ecr.md) §14.7.

---

## 7. FLERT — джерело, яке лишається

FLERT (`Data_Lifecycle` §2.1) — зовнішня БД: часові ряди об'ємів газу і рідин,
денні і місячні, плюс gas composition для всіх потоків, крім тих шести, що
йдуть із HSE400. Словник потоків ведеться файлом `Add Stream_vX_X_X.xlsx`
і процедурою «Add Stream» (`ECR_v_1_17` §10.7).

У БД ECR доступ реалізований синонімами (`Utility_RebindFlertSynonyms`) і
окремим набором RTQP-об'єктів (`PI AF/FLERT_NV_0114_PISqlClientExportedObjects.sql`).

**TO-BE:** окремий `ext.DataSource(Kind = Sql, Code = 'Flert')`, свій адаптер за
тим самим `IExternalDataSource`. Ніяких синонімів і linked server у схемі ядра.
Це джерело **не зникає після переходу** — воно єдине постачає реальні виміри.

---

## 8. Середовища

| Роль | Сервер | Що там |
|------|--------|--------|
| PROD SQL / застосунок | `NCATSQLV92` | БД `ECR` |
| PI AF (PROD) | `NCATAPPV0154` | `ECR_01_Air` |
| PI Web API | `NCATAPPV0160` | запис у AF, PI Vision |
| SSRS | `ncatsqlv42\PI` | `[ReportServer$PI]`, портал `Reports_PI` |
| DEV | `NCATDEVV08` | |
| UAT | `UATV12` | |

`ext.DataSource` заводиться **на середовище**, з окремим рядком підключення в
сховищі секретів; облікові записи, під якими сьогодні працює доступ
(`NCOC\zSvc-PI-NCATSQLV42`, `zSvc-PI-NCATDEVV08`, `zSvc-PI-NCATAPPV0154` —
є в дампі `DB.txt` як користувачі БД), стають сервісними обліковими записами
адаптера (ФВ-6.9). Kerberos-делегування не потрібне — RTQP його не вимагає (ІНТ-10)
і чинна система його не використовує.

---

## 9. Зворотна публікація в AF (ER-I-06)

> ## ⬛ Оновлено 2026-09-03 — ER-I-06 переглянуто
>
> Замовник вирішив **жорсткіше, ніж «вузько»**: **запису в AF немає взагалі** (D-44).
> `IExternalDataSink` не реалізується навіть для довідників. Наслідок для рядка
> «Excel-файли … так, поки живе Excel-введення» у таблиці нижче: довідники в AF для
> ще не мігрованих розділів **лишаються під чинним процесом супроводу** на два періоди
> співіснування (D-45), а не синхронізуються з ECR. Висновок про round-trip
> `;`-рядка **лише для валідації міграції** — підтверджено і став остаточним.


Відповідь на [13] §12 п.2: **потрібна, але вузько і тимчасово.**

Хто читає AF сьогодні:

| Споживач | Що читає | Чи потрібна публікація з ECR |
|----------|----------|------------------------------|
| PI Vision (адмін-UI) | довідники, перміти, методології | **ні** — після переходу редагування переїжджає в ECR |
| `Check_*` аналізи + AFNotificationRule | стан ECR (через атрибути) | **ні** — алерти стають внутрішніми ([B15](B15-ecr-recalc-orchestration.md) §6) |
| Excel-файли (RDS/HSE400/401) | довідники (dropdown), `SETTINGS`, перевірка `File_Number` | **так, поки живе Excel-введення** |
| PI Vision / PI Vision-дашборди для операторів | показники | уточнити (питання I-1) |

Отже: `IExternalDataSink` реалізується **лише для довідників і налаштувань**,
які читає Excel, і **не** для результатів розрахунку. Формат — чинні шаблони AF;
`;`-рядок `Attribute_XXXX` відтворювати **не потрібно**, бо ECR не пише RDS-рядки
назад (їх пише сам Excel, поки він живий).

> Це суттєво менший обсяг, ніж передбачав ТЗ ІНТ-11 («відновити той самий `;`-рядок
> за `LegacyFieldIndex`»). Round-trip `;`-рядка лишається потрібним **лише** для
> **валідації міграції історії** ([B09](B09-bootstrap-and-migration.md) §4.1 крок 6),
> а не для продуктивної публікації.

---

## 10. Реєстр рішень

| # | Рішення | Обґрунтування |
|---|---------|---------------|
| **ER-I-01** | Адаптер не створює артефактів у чужій БД; запит до RTQP формує сам | в'юхи в AF-базі не їдуть із застосунком — джерело поломок при переїзді середовища |
| **ER-I-02** | Склад даних із AF класифіковано за долею; після переходу лишаються **лише сирі виміри і FLERT** | закриває [13] §12 п.3 |
| **ER-I-03** | Ідемпотентність за `(DataSourceId, SourcePath, Timestamp)`; watermark — оптимізація, не стан | втрата watermark не має коштувати даних |
| **ER-I-04** | Майстер довідників → ECR, поетапно через `SourceKind` (`External` → `Hybrid` → `Local`) | закриває [13] §12 п.1; фазування лишає рішення оборотним |
| **ER-I-05** | Зіставлення при міграції — за бізнес-ключем, GUID зберігається **після**, не замість | GUID не переживає перенесення між AF-серверами |
| ~~ER-I-06~~ | ~~`IExternalDataSink` — лише довідники/налаштування для Excel~~ **переглянуто 2026-09-03 → D-44: запису в AF немає взагалі** | замовник обрав жорсткіший варіант; див. блок «Оновлено» у §9 |
| **ER-I-07** | ТЗ §12 п.13 (RTQP) — закрито фактом: уже в продуктиві | 63 процедури читають через `OPENQUERY` до `*_V` |

## 11. Відкриті питання

> ## ⬛ Оновлено 2026-09-03 — рішення прийняті
>
> Питання цього розділу опрацьовані в
> [`../17-open-questions-answers.md`](../design/17-open-questions-answers.md);
> прийняті рішення внесені в [`../06-tz-architecture.md`](../design/06-tz-architecture.md) §12.
> Таблиця нижче лишається як історія; актуальний статус — тут.
>
> | # | Статус | Рішення |
> |---|--------|---------|
> | **I-1** | 🔄 змінив характер | Після рішення S-1 (запису в AF у цільовому стані немає) це **більше не архітектурне питання**. Воно стає пунктом плану переходу: для кожного виявленого споживача (PI Vision, AF Analyses, зовнішні звіти) у runbook з'являється рядок «**на що переведено** (`rpt.*` / API), ким, коли» |
> | **I-2** | ✅ **вирішено** | Перемикати `SourceKind` лише коли всі періоди реєстру у стані `Closed`/`Scheduled` — **ніколи всередині відкритого періоду**. Один період тримати обидва джерела з автоматичним diff і алертом. Перемикання логується як структурна зміна класу `Guarded` |
> | **I-3** | ✅ **вирішено** | Словник потоків FLERT веде **ECR**; файл `Add Stream` переводиться в read-only того ж дня. Реалізація — універсальний реєстр (`../12-reference-data.md`) з одноразовим імпортом. Напівміра «ведемо у файлі, синхронізуємо в ECR» дає найгірший варіант: розбіжності без відповідального |
> | **I-4** | ✅ **вирішено** (замовник, 2026-09-03; переглянуто) | Поточний звітний період — **наша конфігурація**: `doc.Project.CurrentPeriod` із режимами `Auto` (веде `PeriodStateJob`) і `Pinned` (адміністратор, з причиною і аудитом). `AF_CurrentReportPeriod` **не читається і не пишеться** — публікація в AF суперечила б рішенню «запису в AF немає взагалі» (D-44). Використовується як **значення за замовчуванням** (документи, розклади збору, параметри звітів) і **ніколи** для рішень про доступ — вони спираються на стан періоду, інакше «пін» став би прихованим правом редагувати закрите. Різниця моделей: старий період — один на систему, новий — на проєкт; на час співіснування legacy-значення лишається під чинним процесом супроводу. Див. `../tz/10-decisions.md` D-77 |



| # | Питання | Хто | Коли |
|---|---------|------|-----|
| I-1 | Чи є дашборди PI Vision для операторів, які читають результати ECR з AF | PI-адмін | Етап 0 |
| I-2 | Коли перемикати `SourceKind` для кожного реєстру | бізнес + PI-адмін, за розділами | перед кожним розділом фази 1 |
| I-3 | Хто веде словник потоків FLERT після переходу (файл `Add Stream` чи ECR) | бізнес | до розробки FLERT-адаптера |
| I-4 | Чи лишається `AF_CurrentReportPeriod` як глобальний перемикач на час співіснування | архітектор + бізнес | до старту співіснування |

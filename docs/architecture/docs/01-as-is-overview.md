# ECR — Аналіз існуючого рішення (AS-IS)

> Джерела: `RDS_xxx_Template_Land_2026_v1.0.4.0 2.xlsm` (24 аркуші, 4.73 МБ) та `vba-files/`
> (≈45 000 рядків VBA: 55 модулів `.bas`, 33 класи `.cls`, 8 форм `.frm`).
> Версія шаблону: **1.0.4.0**, звітний рік 2026.

---

## 1. Що це таке одним абзацом

`RDS_..._Template_Land_2026.xlsm` — це **майстер-шаблон Excel з макросами**, з якого оператор
генерує окремі робочі файли (**HSE-файли**) для кожного контракту/об'єкта. HSE-файл — це
самодостатня копія книги: вибрані аркуші + повний VBA-проєкт + прив'язані кнопки. Дані з
HSE-файлу помісячно записуються в **PI AF** (OSIsoft/AVEVA Asset Framework) через **PI Web API**
у вигляді Event Frames з атрибутами. Excel виступає одночасно як UI, як движок формул,
як движок валідації і як клієнт інтеграції.

---

## 2. Життєвий цикл (як працює зараз)

```
+--------------------------------------------------------------------------+
| 1. МАЙСТЕР-ШАБЛОН (RDS_..._Template_Land_2026.xlsm)                      |
|    24 аркуші: Configuration, 1. Sheet Selection, 2. Contract,            |
|    18 звітних аркушів (3..11d), DropdownList                             |
+---------------------------+----------------------------------------------+
                            | UserSelection.frm -> cmdSave_Click
                            | (користувач обирає потрібні аркуші зі списку)
                            v
+--------------------------------------------------------------------------+
| 2. ГЕНЕРАЦІЯ HSE-ФАЙЛУ                                                   |
|    Workbooks.Add -> копіювання: Configuration -> 2. Contract ->          |
|    вибрані аркуші -> DropdownList (завжди останній)                      |
|    + CopyVBComponents (експорт/імпорт УСІХ модулів через %TEMP%)         |
|    + ProcessNamedRangesAndValidations / BreakAllExternalLinks            |
|    + AssignShapeMacros (прив'язка кнопок за ТЕКСТОМ на фігурі)           |
|    + SaveAs .xlsm; у Configuration!J4 пишеться GUID нового файлу         |
+---------------------------+----------------------------------------------+
                            |
                            v
+--------------------------------------------------------------------------+
| 3. ЗАПОВНЕННЯ HSE-ФАЙЛУ КОРИСТУВАЧЕМ                                     |
|    Workbook_Open -> InitAllSheets (блокування/розблокування діапазонів)  |
|                  -> CheckServerConnectivity                              |
|                  -> InitDocument (завантаження метаданих з PI AF)        |
|                  -> 5 синхронізацій довідників з AF                      |
|    Worksheet_Change -> валідації, підтвердження, каскадні блокування     |
+---------------------------+----------------------------------------------+
                            | кнопка "Save" (на кожен місяць / таблицю)
                            v
+--------------------------------------------------------------------------+
| 4. ЗБЕРЕЖЕННЯ В PI AF                                                    |
|    Sheet_Config.UniversalSaveHandler -> btrSaveHSE<Sheet>                |
|      |- CheckServerConnectivity / ValidateContractPermitSelected         |
|      |- ValidateStartYearVsContractYear (діапазон років з AF SETTINGS)   |
|      |- InitDocument (WebId шаблонів / елементів / атрибутів)            |
|      |- CheckExistEF -> якщо є: Overwrite / Skip / Ask                   |
|      |- AddEventFrameService (створення Event Frame)                     |
|      |- MapAttributes (рядок аркуша -> рядок "v1;v2;v3;...")             |
|      +- WriteAttrDataService (POST streamsets/value)                     |
+--------------------------------------------------------------------------+
```

---

## 3. Аркуші майстер-шаблону

| # | Аркуш | Роль | Формул | maxRow | Захист |
|---|-------|------|--------|--------|--------|
| 1 | `Configuration` | Конфіг + локальні довідники (кеш AF) | 129 | 77 | ні |
| 2 | `1. Sheet Selection` | Порожній, лише кнопка запуску `UserSelection` | 0 | 0 | ні |
| 3 | `2. Contract` | Шапка/метадані звіту (13 полів B6:B18) | 0 | 18 | так |
| 4 | `3. Stationary Equipment` | Звітна таблиця | 0 | 838 | так |
| 5 | `4. Mobile Equipment` | Звітна таблиця | 0 | 7 | так |
| 6 | `5. Other Equipment` | 5 таблиць в одному аркуші | 0 | 396 | так |
| 7 | `6. Dust Producing Activities` | 7 таблиць | 0 | 82 | так |
| 8 | `7.0 Water consolidation` | **Тільки формули** (rollup з `7.`) | 306 | 19 | так |
| 9 | `7. Water Report` | Звіт по воді (GROUP / ITEM / BALANCE / NOTE) | 394 | 109 | так |
| 10 | `7a.Water measurements` | 16 таблиць вимірювань | 191 | 157 | так |
| 11 | `7b.Water measurements` | 1 таблиця | 100 | 9 | так |
| 12 | `8. Waste Report` | 12 блоків = 12 місяців **по рядках** | 6 503 | 398 | так |
| 13 | `9. Utility` | 15 таблиць | 1 536 | 471 | так |
| 14 | `9a. Process units` | 17 таблиць | 26 | 309 | так |
| 15 | `9b. Sulfur` | 1 таблиця | 264 | 52 | так |
| 16 | `9c. Fugitive leaks` | Довідкова, у save-flow не бере участі | 0 | 320 | ні |
| 17 | `10. Off_Utility_DI` | 4 таблиці | 0 | 82 | так |
| 18 | `10a. Off_Utility_AI` | 2 таблиці | 0 | 13 | так |
| 19 | `11. Off_Process_DI` | 5 таблиць | 0 | 97 | так |
| 20 | `11a. Off_Process_AI` | 1 таблиця | 0 | 32 | так |
| 21 | `11b. Off_Process_EPC2` | 1 таблиця | 27 | 29 | так |
| 22 | `11c. Off_Process_EPC3` | 1 таблиця | 36 | 26 | так |
| 23 | `11d. Off_Process_EPC4` | 1 таблиця | 19 | 236 | так |
| 24 | `DropdownList` | Джерело значень для named ranges | 0 | 150 | ні |

**Разом: ~10 000 формул, ~440 000 комірок, 287 defined names, ~300 data validations,
~900 conditional formats, 16 external links.**

### 3.1 Верифіковані факти (автоматичний аналіз файлу)

Отримано розбором `.xlsx`-структури, не оцінкою.

**Крос-аркушні залежності — усього 7 ребер:**

| Кількість формул | Залежність | Тип |
|-----------------:|------------|-----|
| 273 | `7. Water Report` -> `Configuration` | VLOOKUP у довідники |
| 216 | `7.0 Water consolidation` -> `7. Water Report` | Rollup |
| 191 | `7a.Water measurements` -> `7. Water Report` | Mirror |
| 156 | `8. Waste Report` -> `Configuration` | VLOOKUP у довідники |
| 100 | `7b.Water measurements` -> `7. Water Report` | Mirror |
| 1 | `7. Water Report` -> `2. Contract` | рік зі строки версії |
| 1 | `8. Waste Report` -> `2. Contract` | те саме |

> **Решта 18 аркушів не мають крос-аркушних залежностей взагалі.**
> Граф простіший, ніж виглядає: три `Rollup`/`Mirror` зв'язки плюс довідники.

**Excel-функції — усього 11 різних:**

`VLOOKUP` 429 · `LEFT` 384 · `SUM` 245 · `VALUE` 230 · `IFERROR` 228 ·
`ROUNDDOWN` 5 · `TEXT` 3 · `CHAR` 3 · `CONCATENATE` 2 · `TEXTBEFORE` 2 · `TEXTAFTER` 2

⚠ 429 `VLOOKUP` — це **не обчислення, а посилання на довідники**
(`VLOOKUP(D6, Configuration!$R$2:$U$11, 2, FALSE)`). У новій моделі вони
зникають — їх замінює прив'язка колонки до реєстру. Реальних обчислювальних
формул лишається **~250**.

**«Мертвий вантаж» у файлі:**

| Що | Обсяг | Стан |
|----|-------|------|
| `vbaProject.bin` | 2.99 МБ | копіюється в **кожен** HSE-файл |
| `styles.xml` | 1.23 МБ | ~40 змістовних стилів, роздутих до тисяч записів |
| `externalLinks/*` | **6.41 МБ**, з них один — 5.9 МБ | **у формулах не використовується жодного разу** |
| defined names | 287, з них **87 (30%) із `#REF!`** | вказують у нікуди |

16 зовнішніх книг згадуються лише в named ranges
(`'[2]DropdownList 2015'`, `'[5]Attachment 5'`), у формулах — нуль посилань.

> При міграції це не переноситься. Bootstrap-імпортер має видати звіт
> про невимпортоване, щоб бізнес підтвердив, що нічого потрібного не втрачено.

---

## 4. Ключова структурна ідея: «аркуш = набір таблиць»

Аркуш **не** є однією таблицею. На одному аркуші розміщено від 1 до 17 логічних таблиць.
Кожна таблиця має:

* **`templateRow`** — номер рядка-заголовка, який ідентифікує таблицю
  (напр. `9. Utility` -> рядки `1,12,45,52,241,261,275,371,401,411,431,440,446,459,465`);
* **PI AF Event Frame Template** — `Configuration.GetTemplateNameForSheet(sheet, templateRow)`
  (напр. `"9. Utility"|241` -> `HSE_Land_Utility_9_05`);
* **PI AF Element Template** + **Element Name** — місце прив'язки EF у дереві AF.

Ця відповідність жорстко зашита в `Module/Configuration.bas` (словники `Template` /
`ElementTemplate` / `ElementName`), продубльована в `Module/BulkSave.bas` (реєстр)
і частково в `Module/Action.bas` (`InitTemplatesMap`).

> **Це головний кандидат на винесення в БД як «конфігуратор шаблонів».**

---

## 5. Місяці

Дві моделі розміщення місяців:

| Модель | Аркуші | Як влаштовано |
|--------|--------|----------------|
| **Місяці по колонках** | усі, крім `8. Waste Report` | Січень…Грудень = колонки; кнопка «Save» стоїть у комірці з назвою місяця, `btnShape.topLeftCell.Value` дає місяць |
| **Місяці по рядках** | `8. Waste Report` | 12 блоків по 33 рядки: рядок 3 = Січень, 36 = Лютий, …, 366 = Грудень (`3 + (m-1)*33`) |

`7. Water Report` — місяці в колонках **I…T** (9…20). Колонки F (Permit number) і
G (Name object) були вставлені пізніше і зсунули діапазон — у коді про це є коментарі.

---

## 6. Точки інтеграції з PI AF

| Операція | Функція | HTTP |
|----------|---------|------|
| Ініціалізація метаданих | `InitData.InitDocument` -> `CreateInitRequestBody` | `POST /batch` (5 підзапитів) |
| Пошук існуючих EF | `BackEndService.CheckExistEF(Service)` | `POST /batch` -> `eventframes/search` |
| Створення EF | `BackEndService.AddEventFrameService` | `POST /batch` (create + read + read attrs) |
| Видалення EF | `BackEndService.DeleteEventFrameService` | `DELETE /eventframes/{webId}` |
| Запис значень | `BackEndService.WriteAttrDataService` | `POST /streamsets/value?updateOption=replace` |
| Читання року | `BackEndService.GetEFYearAndValuesService` | `POST /batch` |
| Довідники | `DictionaryEF`, `WasteReportMetricSync`, `PermitConstantSync`, `PermitWaterSync`, `WR_DictionarySync` | `POST /batch` |
| Логування | `Logger.LogToPI` | створення EF шаблону `Log` |

Автентифікація — **Windows (NTLM / Kerberos)** через WinHTTP:
`clsWindowsAuthenticator.IWebAuthenticator_PrepareHttp` -> `Http.SetAutoLogonPolicy 0 (Always)`.

Failover: `Configuration!B3` (primary) / `B12` (secondary), TTL кешу вибору — `B13` (60 000 мс),
логіка в `InitData.ApiBase` / `ProbeApiBase`.

---

## 7. Конфігурація (аркуш `Configuration`)

| Комірка | Ім'я | Значення в шаблоні |
|---------|------|--------------------|
| `B1` | SERVER | `NCATDEVV08` |
| `B2` | DB | `ECR_01_Air` |
| `B3` | BASE_URL (primary) | `=CONCATENATE("https://",B1,"/piwebapi/")` |
| `B12` | SECONDARY_URL | `https://ncatdevv08/piwebapi/` |
| `B13` | TTL | `60000` |
| `B4` | TEMPLATE | порожньо -> береться з `Configuration.bas` |
| `B5` | ELEMENT_TEMPLATE | порожньо |
| `B6` | REPORT_VIEW_URL | `=CONCATENATE("http://",B1,"/Reports/")` |
| `B8` | Version | `1.0.4.0` |
| `B9` | FILE_LOGGING | `True` |
| `B10` | PI_LOGGING | `False` |
| `B11` | AUTO_SAVE | `False` |
| `I2` | IsBlockData | `True` (прапорець «InitDocument вже виконано») |
| `I3` | Location | `Land` |
| `J3` | GUID з AF (збігається з ID дозволу `KZ06VCZ14825472` у `DropdownList!AL5`; точне призначення уточнити при bootstrap-імпорті) | `c4eed347-52a5-11f1-…` |
| `J4` | GUID нового файлу | генерується при `SaveNewWorkbook` |
| `K3` | Рік | `2026` |
| `L1..L8` | Часові мітки синхронізацій довідників | |

Колонки `R…BL` — **локальний кеш довідників з AF**:

| Діапазон | Зміст |
|----------|-------|
| `R:U` | Water groups (Name_EN, Entry_Id, Entry_Type, Name_RU) |
| `V:Y` | Water items |
| `Z:AC` | Water balances |
| `AD:AG` | Notes |
| `AH` | `Item_Display` = `TEXT(V,"0") & " - " & W` |
| `AJ:AP` | Waste items (Entry_Id, Group_Id, назви EN/RU, Item_Display) |
| `AQ:AV` | Substances (код, назва EN/RU, Density, Type_hazard, Header_Display) |
| `AW:AZ` | **Permits**: PermitId (GUID), Permit_Number, Start_Date, Actual_End_Date |
| `BA:BB` | Water bodies: Name_water_body_Id (GUID), Name_En |
| `BD:BL` | Матриця Permit × Water body (тип: intake / discharge) |

---

## 8. Правила доступу та блокувань (як зараз)

Ролей немає. Є **пароль на аркуші** `"1qazxcde3"` (у відкритому коді) і кнопка
`Protection.ToggleProtection`, яка:

1. Запитує пароль через `frmPassword` + «день відсічення» (cutoff day);
2. Обчислює «редагований місяць» = `GetEditableMonthName(cutoffDay, Date)`;
3. Блокує ВСІ комірки всіх аркушів;
4. Розблоковує лише колонки редагованого місяця;
5. Повторно блокує рядки-заголовки (жорстко зашиті номери рядків для кожного аркуша);
6. Для `7. Water Report` додатково перевіряє **вікно дії дозволу (permit)** по кожному рядку
   (`Sheet_Config.ApplyPermitMonthLocks`).

Тобто «доступ» = «який місяць зараз відкритий для редагування», а не «хто що може».

---

## 9. Валідація (як зараз)

| Рівень | Механізм | Приклад |
|--------|----------|---------|
| Комірка | Excel Data Validation | `type=list f1=Generic_EquipStatus`, `type=decimal f1=744` (годин у місяці) |
| Комірка | `General.ValidateCellValue` | заборона спецсимволів |
| Комірка | `General.EnforceNumericOnly` | лише числа в діапазоні |
| Рядок | `Worksheet_Change` у класах аркушів | підтвердження зміни permit + очищення місяців поза вікном дії |
| Аркуш | `Sheet_Config.Init*` | які діапазони заблоковані |
| Документ | `Action.ValidateContractPermitSelected` | permit на `2. Contract` обраний і резолвиться |
| Документ | `General.ValidateStartYearVsContractYear` | рік звіту в межах `[CALCULATION_STARTDATETIME, CALCULATION_ENDDATETIME]` з AF |
| Документ | `modB16AmendmentFlow` | зміна `2. Contract!B16` (File Number) -> пошук EF за старим/новим номером, звіт користувачу |

**Немає** процесу approval / затвердження. Дані вважаються дійсними одразу після запису в AF.

---

## 10. Bulk save

`BulkSave.bas` — дві кнопки на `2. Contract`: «Save all months» / «Save current month».
Формує декартів добуток `аркуш × templateRow × місяць`, знаходить **реальну кнопку-фігуру**
для кожної комбінації і викликає `btrSaveHSE*` з параметрами
`(overrideButtonShape, silentMode:=True, existingEfPolicy)`.

Причина такого рішення: `btrSaveHSE*` визначає місяць/таблицю через `Application.Caller`,
а підробити клік по фігурі з VBA неможливо.

Підтримуються 18 аркушів. `7.0 Water consolidation` навмисно не входить — його зберігає
`HSE_WaterReport` автоматично після власного успішного збереження.

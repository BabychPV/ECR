# Інвентаризація VBA-коду

Всього ~45 000 рядків. Нижче — карта модулів із зазначенням, що з цим робити при міграції.

Легенда стовпця «Доля»:
* **BL** — бізнес-логіка, переноситься в C# (Domain / Application layer)
* **INFRA** — інфраструктура, замінюється стандартними засобами .NET
* **CFG** — конфігурація/дані, переноситься в SQL
* **UI** — UI, замінюється React SPA
* **DROP** — зникає (Excel-специфічне, не потрібне)

---

## 1. Ядро інтеграції з PI Web API

| Файл | Рядків | Що робить | Доля |
|------|--------|-----------|------|
| `Module/BackEndService.bas` | 1 317 | CRUD Event Frames, запис атрибутів, побудова JSON batch-запитів вручну через конкатенацію рядків | **BL** -> `Ecr.Adapters.PiAf` (`IPiAfDataReader`: Web API + SQL DAS, типізовані DTO) |
| `Module/MakePIWEBAPIRequest.bas` | 83 | Обгортка `MakeRequest(endpoint, method, body)`, синхронізація baseUrl при failover | **INFRA** -> `HttpClient` + Polly |
| `Class/clsWebClient.cls` | 719 | HTTP-клієнт (порт бібліотеки VBA-Web) | **INFRA** -> `IHttpClientFactory` |
| `Class/clsWebRequest.cls` | 833 | Побудова запиту | **INFRA** |
| `Class/clsWebResponse.cls` | 380 | Парсинг відповіді | **INFRA** |
| `Class/clsWindowsAuthenticator.cls` | 69 | NTLM/Kerberos: `SetAutoLogonPolicy Always` | **INFRA** -> `HttpClientHandler{UseDefaultCredentials=true}` |
| `Class/IWebAuthenticator.cls` | 71 | Інтерфейс автентифікатора | **DROP** |
| `Module/WebHelpers.bas` | 3 155 | Утиліти VBA-Web (URL encode, JSON, логування) | **DROP** |
| `Module/JsonConverter.bas` | 1 094 | JSON-парсер (VBA-JSON) | **DROP** |
| `Class/Dictionary.cls` | 474 | Реалізація словника | **DROP** |
| `Module/DecodeUTF.bas` | 51 | Декодування UTF | **DROP** |

**Разом ~8 200 рядків (18%) — це чиста інфраструктура, яка в .NET зникає повністю.**

---

## 2. Конфігурація та метадані

| Файл | Рядків | Що робить | Доля |
|------|--------|-----------|------|
| `Module/Configuration.bas` | 282 | **Мапінг `sheet [+ templateRow] -> AF Template / ElementTemplate / ElementName`** | **CFG** -> `cfg.SheetDef`/`cfg.TableDef` (структура) + `ext.LegacyTableMapping` (AF-шаблони — поза ядром) |
| `Module/InitData.bas` | 847 | Глобальні змінні, завантаження метаданих AF (WebId), failover, налаштування логування | **BL + CFG** |
| `Module/Sheet_Config.bas` | 1 330 | `Init*` для кожного аркуша: які діапазони заблоковані, dropdown-и, permit-локи | **CFG** -> `ColumnDef.IsReadOnly` + `cfg.PeriodAccessRuleDef` + `ColumnDef.LookupRegistryDefId` |
| `Module/Action.bas` | 637 | `Save`, читання `2. Contract`, побудова URL звіту, `InitTemplatesMap` | **BL** |

---

## 3. Збереження звітних аркушів (найбільший блок)

22 модулі `HSE_*.bas`, ~14 000 рядків. **Це майже дублікати** з різницею в
координатах комірок і наборі колонок.

| Файл | Рядків | Аркуш | templateRows |
|------|--------|-------|--------------|
| `HSE_Stationary_Equipment.bas` | 638 | 3. Stationary Equipment | 0 |
| `HSE_Mobile_Equipment.bas` | 603 | 4. Mobile Equipment | 0 |
| `HSE_Other_Equipment.bas` | 663 | 5. Other Equipment | 1,15,29,43,57 |
| `HSE_Dust_Producing_Activities.bas` | 669 | 6. Dust Producing Activities | 1,15,29,43,57,71,76 |
| `HSE_WaterConsolidation.bas` | 578 | 7.0 Water consolidation | 0 |
| `HSE_WaterReport.bas` | 624 | 7. Water Report | 0 |
| `HSE_WaterMeasurements.bas` | 648 | 7a.Water measurements | 3,22,…,150 (16) |
| `HSE_WaterMeasurements7b.bas` | 585 | 7b.Water measurements | 5 |
| `HSE_WasteReport.bas` | 974 | 8. Waste Report | 3,36,…,366 (12 = місяці) |
| `HSE_Utility.bas` | 798 | 9. Utility | 15 таблиць |
| `HSE_Process_Units.bas` | 711 | 9a. Process units | 17 таблиць |
| `HSE_Sulfur.bas` | 623 | 9b. Sulfur | 0 |
| `HSE_Off_Utility_DI.bas` | 720 | 10. Off_Utility_DI | 1,21,41,62 |
| `HSE_Off_Utility_AI.bas` | 654 | 10a. Off_Utility_AI | 1,6 |
| `HSE_Off_Process_DI.bas` | 745 | 11. Off_Process_DI | 1,13,54,78,89 |
| `HSE_11a_Off_Process_AI.bas` | 632 | 11a. Off_Process_AI | 0 |
| `HSE_11b_Off_Process_EPC2.bas` | 627 | 11b. Off_Process_EPC2 | 0 |
| `HSE_11c_Off_Process_EPC3.bas` | 626 | 11c. Off_Process_EPC3 | 0 |
| `HSE_11d_Off_Process_EPC4.bas` | 627 | 11d. Off_Process_EPC4 | 0 |
| `HSE_373_Air.bas` | 462 | (legacy, поза BulkSave) | — |
| `HSE_400_Air.bas` | 204 | (legacy) | — |
| `HSE_401_Air.bas` | 605 | (legacy) | — |

**Кожен модуль має однакову структуру:**

```vba
Function btrSaveHSE<Sheet>(overrideButtonShape, silentMode, existingEfPolicy) As String
    ' 1. CheckServerConnectivity
    ' 2. визначити sheetName / monthName / templateRow / templateName
    ' 3. ValidateContractPermitSelected + ValidateStartYearVsContractYear
    ' 4. InitDocument -> elementWebId
    ' 5. CheckExistEF -> Skip / Overwrite / Ask
    ' 6. AddEventFrameService
    ' 7. MapAttributes -> Dictionary(attrWebId -> value)
    ' 8. WriteAttrDataService
    ' 9. UI (колір кнопки, protect, progress form)
End Function

Private Function MapAttributes(...) As Object
    ' для кожного рядка таблиці:
    '   rowNumber = Format(rowOffset * 10, "0000")
    '   partList = [значення колонок…]
    '   foundAttributes("Attribute_" & rowNumber) = Join(partList, ";") & ";"
    ' + merge з GetContractSheetAttributes()
    ' + зіставлення імен атрибутів з WebId з відповіді PI
End Function
```

> **У новій системі це — ОДИН generic-сервіс `DataEntryService`, керований метаданими
> з БД.** 14 000 рядків -> ~800 рядків.

---

## 4. Синхронізація довідників з AF

| Файл | Рядків | Довідник |
|------|--------|----------|
| `Module/DictionaryEF.bas` | 1 547 | `DropdownList` + `PermitDocument` індекс (`Dictionary_Land`) |
| `Module/WR_DictionarySync.bas` | 794 | Water Report items/groups/balances/notes |
| `Module/WasteReportMetricSync.bas` | 694 | Waste Report метрики |
| `Module/PermitWaterSync.bas` | 837 | Permits (номер, дати дії) + Water bodies |
| `Module/PermitConstantSync.bas` | 574 | Речовини (substances) |

Всі п'ять викликаються з `Workbook_Open` з throttling за таймстемпами в `Configuration!L1:L8`.

**Доля:** -> **один** generic-синхронізатор, керований `cfg.RegistrySyncMapping`
(джерело, шаблон, мапінг полів, розклад, поведінка при зникненні запису).
4 400 рядків -> конфігурація + ~300 рядків коду.

---

## 5. Загальні утиліти та бізнес-правила

| Файл | Рядків | Що робить | Доля |
|------|--------|-----------|------|
| `Module/General.bas` | 1 317 | Дати, UTC-конверсія, валідації року, `HandleWorksheetChange`, `ShowPermitConfiguration` | **BL** |
| `Module/ConvertData.bas` | 480 | Конвертація типів/дат, `CheckAndReturnValue` | **BL** |
| `Module/Protection.bas` | 536 | Блокування місяців за cutoff day + permit-вікна | **CFG** -> `cfg.PeriodAccessRuleDef` (`HeaderRows`, `SourceWindow`…) + `doc.PeriodPolicy` / `PeriodStateJob` |
| `Module/BulkSave.bas` | 438 | Масове збереження `аркуш × таблиця × місяць` | **BL** -> batch job |
| `Module/GetReportName.bas` | 354 | Вибір Report ID зі списку | **BL** |
| `Module/modB16AmendmentFlow.bas` | 764 | Реакція на зміну File Number: пошук EF по старому/новому | **CFG** -> `TableRelationDef.OnSourceChange = Warn` на `MetadataSource`-зв'язку |
| `Module/Logger.bas` | 484 | Логування у файл + у PI AF | **INFRA** -> Serilog |
| `Module/FindExternalLinks.bas` | 471 | Пошук/розрив зовнішніх посилань | **DROP** |
| `Module/FixWaterConsolidation.bas` | 67 | Виправлення формул консолідації | **DROP** |
| `Module/AddComment_Manual.bas` | 450 | Робота з коментарями комірок | **DROP** |
| `Module/Add_Boat.bas` | 81 | Додавання рядка | **DROP** |
| `Module/ShowSheetSelector.bas` | 37 | Показ форми | **UI** |
| `Module/Test_Save.bas` | 618 | Тести | **DROP** |
| `Module/modHSE_TestCore.bas` | 2 078 | Тестовий фреймворк | **DROP** (замінюється xUnit) |

---

## 6. Permits / дозволи

| Файл | Рядків | Що робить |
|------|--------|-----------|
| `Module/AddPermitAir.bas` | 536 | Створення дозволу «повітря» в AF |
| `Module/AddPermitWater.bas` | 530 | Створення дозволу «вода» в AF |
| `Module/AddPollutantEmissionFees.bas` | 409 | Ставки плати за викиди |
| `Module/PollutantEmissionFees.bas` | 69 | Обгортка |
| `Form/frmPermitAir.frm` | 221 | Форма дозволу «повітря» |
| `Form/frmPermitWater.frm` | 179 | Форма дозволу «вода» |
| `Form/frmPollutantEmissionFees.frm` | 128 | Форма ставок |

**Доля:** ці ~2 100 рядків **не переписуються, а зникають** — `Permit` стає
конфігурацією універсального реєстру (`RegistryDef` + `Composition` -> `PermitPollutant`
+ `Association` -> `WaterBody` + `ValidityWindow`), який обслуговує один generic-CRUD.
Разом із 5 модулями синхронізації (≈4 400 рядків) це **≈7 800 рядків -> конфігурація**.
Див. [12-reference-data.md](12-reference-data.md).

---

## 7. UI-форми

| Файл | Рядків | Що робить | Доля |
|------|--------|-----------|------|
| `Form/UserSelection.frm` | 1 149 | **Генератор HSE-файлу**: вибір аркушів, копіювання, копіювання VBA, прив'язка макросів, SaveAs + GUID | **BL** -> `DocumentGenerator` + `cfg.SheetGroupRule` (замість `ExpandLinkedSheetGroups`) |
| `Form/frmProgress.frm` | 74 | Прогрес-бар | **UI** |
| `Form/frmPassword.frm` | 78 | Пароль + cutoff day | **UI** -> RBAC |
| `Form/frmSelectReports.frm` | 43 | Вибір звітів | **UI** |
| `Form/frmAmendmentWarning.frm` | 71 | Попередження про amendment | **UI** |

---

## 8. Класи аркушів (обробники подій)

`Class/Sheet1..Sheet10.cls`, `Class/Worksheet____1..16.cls` — ~2 500 рядків.
Містять `Worksheet_Change`, `Worksheet_SelectionChange`, `Worksheet_Activate`.

Найважливіші:
* `Sheet1.cls` (198) — `2. Contract`: відстеження зміни `B16` (File Number) -> `modB16AmendmentFlow`
* `Sheet9.cls` (757) — найбільший, логіка одного зі звітних аркушів
* `Worksheet____1.cls` (235) — `7. Water Report`: підтвердження зміни permit, каскадне
  блокування/очищення місяців поза вікном дії дозволу
* `ThisWorkbook.cls` (110) — `Workbook_Open`: ініціалізація + 5 синхронізацій

**Доля:** -> серверні правила валідації, керовані метаданими (`ValidationRule`),
плюс клієнтські підказки.

---

## 9. Підсумок: що куди

| Категорія | Рядків | % | Доля |
|-----------|--------|---|------|
| HTTP / JSON / Dictionary інфраструктура | ~8 200 | 18% | зникає (стандартний .NET) |
| Save-модулі `HSE_*` (дублікати) | ~14 000 | 31% | -> 1 generic-сервіс, ~800 рядків |
| Синхронізація довідників | ~4 400 | 10% | -> `RegistrySyncMapping` + один generic-синхронізатор |
| Тести/дебаг VBA | ~2 700 | 6% | -> xUnit |
| Excel-специфічне (лінки, коментарі, захист) | ~2 000 | 4% | зникає |
| Реальна бізнес-логіка (валідації, дати, permits, amendment, bulk) | ~7 000 | 16% | **переноситься** |
| Конфігурація/мапінги | ~2 000 | 4% | **-> SQL** |
| UI-форми | ~1 700 | 4% | -> веб |
| Класи аркушів / інше | ~3 000 | 7% | частково |

> **Висновок: реальної бізнес-логіки ~20% коду. Решта — інфраструктура,
> дублювання та боротьба з обмеженнями Excel.**

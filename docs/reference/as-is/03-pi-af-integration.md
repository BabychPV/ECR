# Інтеграція з PI AF — як зараз зберігаються дані

Це найважливіший документ для розуміння, **чому** потрібна міграція на SQL.

---

## 1. Модель даних PI AF (as-is)

```
AF Server (Configuration!B1 = NCATDEVV08)
 └─ AF Database (Configuration!B2 = ECR_01_Air)
     ├─ Elements (дерево об'єктів)
     │   └─ <ElementName>            напр. "Land_07", "Utility_9_05"
     │       ElementTemplate         напр. "HSE_Land_07_Template"
     │
     ├─ ElementTemplates
     │   └─ HSE_Land_07              (шаблон Event Frame)
     │       ├─ Attribute_0010 : String
     │       ├─ Attribute_0020 : String
     │       ├─ …
     │       ├─ Attribute_1160 : String
     │       ├─ File_Number    : String   (метадані з 2. Contract)
     │       ├─ Contract_Number: String
     │       ├─ Permit_Number  : String
     │       ├─ File_Name      : String
     │       ├─ Sheet_Name     : String
     │       └─ …
     │
     ├─ EventFrames  ← ТУТ ЛЕЖАТЬ ДАНІ
     │   └─ "<EFName>_<user>_<yyyy_mm_dd_HH_MM_SS>"
     │       Template   = HSE_Land_07
     │       StartTime  = EndTime = перше число звітного місяця (ISO, UTC)
     │       Severity   = Major
     │       RefElementWebIds = [elementWebId]
     │       Attributes = значення Attribute_XXXX
     │
     └─ SETTINGS\GENERAL
         ├─ CALCULATION_STARTDATETIME   (межа звітного періоду, «рік»)
         └─ CALCULATION_ENDDATETIME
```

---

## 2. Критична деталь: рядок таблиці = ОДИН строковий атрибут

Це головна архітектурна проблема поточного рішення.

`MapAttributes` у кожному `HSE_*.bas` робить так:

```vba
rowOffset = 0
For i = startRow To endRow
    rowOffset = rowOffset + 1
    rowNumber = Format(rowOffset * 10, "0000")   ' 0010, 0020, 0030, …

    Set partList = New Collection
    partList.Add CellValueForExport(sheet.Cells(i, 2))   ' B
    partList.Add CellValueForExport(sheet.Cells(i, 3))   ' C
    …
    partList.Add CellValueForExport(sheet.Cells(i, monthCol))

    foundAttributes("Attribute_" & rowNumber) = Join(partList, ";") & ";"
Next i
```

Тобто **весь рядок таблиці серіалізується в один рядок, розділений `;`**:

```
Attribute_0010 = "1;7001001;ITEM;094f83a4-9560-…;0151202a-9c95-…;125.5;340.2;1;0;0;0;"
                  |  |       |    |                |                |     |     |
                  A  B(ID)   C    F->PermitId      G->WaterBodyId   H     міс.  прапорці
```

### Наслідки

| Проблема | Опис |
|----------|------|
| **Немає типізації** | Усі значення — рядки. Числа, дати, GUID, булеві — все одне |
| **Немає запитів** | Неможливо зробити «покажи всі рядки, де споживання > X» без повного вивантаження й парсингу |
| **Немає індексів** | Пошук по File_Number = вивантаження ВСІХ EF за рік і фільтрація в клієнті (`BuildEFMonthTreeByFileNumberFast`, maxCount=1000) |
| **Крихкість** | Вставка колонки в Excel зсуває всі позиції -> старі й нові EF несумісні. У коді є коментарі про це для `7. Water Report` |
| **Немає версійності** | «Оновлення» = DELETE EF + CREATE новий EF. Історія втрачається |
| **Немає цілісності** | Нічого не гарантує, що `PermitId` у рядку існує |
| **Escaping** | JSON будується конкатенацією рядків; значення з `"` чи `\` ламають запит. Є hotfix через `JsonConverter.ConvertToJson` для значень |

---

## 3. Формат batch-запиту (PI Web API)

Приклад створення Event Frame — `AddEventFrameService`:

```json
{
  "id_1": {
    "Method": "POST",
    "Resource": "{BASE_URL}assetdatabases/{SERVERWEBID}/eventframes",
    "Content": "{"Name":"<EFName>_<user>",
                 "Description":"Flert Event",
                 "TemplateName":"HSE_Land_07",
                 "StartTime":"<iso>",
                 "EndTime":"<iso>",
                 "Severity":"Major",
                 "RefElementWebIds":["<elementWebId>"]}",
    "Headers": {"Content-Type": "application/json"}
  },
  "id_2": {
    "Method": "GET",
    "Resource": "{0}?selectedFields=ExtendedProperties;StartTime;EndTime;WebId;Links.Value",
    "Parameters": ["$.id_1.Headers.Location"],
    "ParentIds": ["id_1"]
  },
  "id_3": {
    "Method": "GET",
    "Resource": "{0}?selectedFields=Items.Name;Items.WebId;Items.Value.Value",
    "Parameters": ["$.id_2.Content.Links.Value"],
    "ParentIds": ["id_2"]
  }
}
```

Далі `id_3` повертає список атрибутів EF з їхніми `WebId`. Код зіставляє
`Attribute_XXXX` -> `WebId` і формує запис:

```json
POST /streamsets/value?updateOption=replace
[
  {"WebId":"<attrWebId>", "Value":{"Timestamp":"2026-09-02T12:00:00Z", "Value":"1;7001001;ITEM;…"}},
  …
]
```

### Обробка статусів

* Top-level `StatusCode` <= 207 -> успіх (інакше `MakeRequest` повертає `Nothing`)
* `id_1` (create EF): 200/201/202 -> ок; 401/403 -> «Access Denied»
* `id_2` (batch sub-requests): 207 -> multi-status
* `id_3` (read attrs): 200
* Запис: 200/202/204/207

---

## 4. Пошук існуючих Event Frames

```
GET eventframes/search/?databaseWebId={db}
    &query= Start:="<iso>" Name:="<prefix>*" Template:="<tpl>"
    &maxCount=100
    &selectedFields=Items.WebId;Items.Links.Value
```

Далі `id_2` (RequestTemplate) читає значення атрибутів для кожного знайденого EF.

**Фільтрація по File_Number робиться на клієнті** — сервер не вміє фільтрувати по значенню
атрибута. `BuildEFMonthTreeByFileNumberFast` вивантажує до 1000 EF за рік і перебирає їх
у VBA-циклі. Для 200 файлів × 18 аркушів × 12 місяців це десятки тисяч EF.

> Це ключова причина скарг на продуктивність.

---

## 5. Іменування Event Frame

`General.GenerateEventFrameName(monthName)` + суфікси:

```
<baseName>_<userName>_<yyyy_mm_dd_HH_MM_SS>
```

де `baseName` формується з File Number / місяця, `userName` = `Environ("Username")`.

Таким чином:
* **час створення закодований в імені** (а не в полі);
* **автор закодований в імені** (а не в полі);
* пошук ведеться по префіксу `Name:="<prefix>*"`.

---

## 6. Довідники в AF

Довідники теж зберігаються як Event Frames зі спеціальним шаблоном:

| Шаблон AF | Синхронізатор | Куди в Excel |
|-----------|---------------|--------------|
| `Dictionary_Land` | `DictionaryEF.DropdownListUpdate` | аркуш `DropdownList` |
| `PermitDocument` | `DictionaryEF.UpdatePermitDocumentColumnJ` | `DropdownList!J` |
| `HSE_Permit_Water` | `PermitWaterSync` | `Configuration!AW:BB` |
| `HSE_Pollutant_Emission_Fees_T5` | `PermitConstantSync` | `Configuration!AQ:AV` |
| `HSE_Land_08` (metrics) | `WasteReportMetricSync` | `Configuration!AJ:AP` |
| Water Report dictionary | `WR_DictionarySync` | `Configuration!R:AH` |

Кожен запис довідника має атрибут `Is_Available` (bool). Синхронізація:
1. Вивантажити всі EF шаблону;
2. Прибрати з Excel значення, яких немає в AF або де `Is_Available = False`;
3. Дописати відсутні;
4. Перебудувати named range;
5. Записати таймстемп в `Configuration!L*`.

---

## 7. Логування

`Logger.bas` пише в два місця:

* **Файл** — `ThisWorkbook.Path\PI_Integration.log`, ротація по 5 МБ (`Configuration!B9`)
* **PI AF** — Event Frame шаблону `Log` з атрибутами `Entity`, `Message`, `Status`, `Time`,
  `Type_Action`, `User_Name` (`Configuration!B10`, зараз `False`)

---

## 8. Що зберегти при міграції

| Що | Чому |
|----|------|
| **Windows Authentication до PI** | Зберігається, але **під сервісним обліковим записом**: локальні користувачі не мають Windows-ідентичності, а RTQP делегування не потребує. Реальний користувач фіксується в аудиті ECR |
| **Batch-запити** | Один HTTP-виклик замість N — критично для продуктивності |
| **Primary/Secondary failover** | Вже є в проді, працює |
| **`SETTINGS\GENERAL\CALCULATION_*`** | Джерело істини про відкритий звітний період |
| **Довідники як джерело істини в AF** | Змінювати їх у новій системі — окреме рішення (див. ТЗ, п. «Відкриті питання») |

## 9. Що змінити

| Що | Як |
|----|-----|
| Серіалізація рядка в `;`-рядок | Нормальні таблиці SQL, одна комірка = один рядок у `CellValue` |
| Delete+Create для оновлення | Версійність + audit trail у SQL |
| Клієнтська фільтрація по File_Number | SQL-індекси у нашій БД (`doc.DocumentIndexValue`); для читання з AF — PI SQL DAS з `WHERE` на боці PI |
| Ручна конкатенація JSON | Типізовані DTO + `System.Text.Json` |
| PI AF як OLTP-сховище | **SQL — система обліку; PI AF — джерело даних** (read-only, два транспорти: Web API і SQL DAS). Зворотна публікація — лише опційний адаптер на перехідний період (ТЗ §5.3, ІНТ-11) |

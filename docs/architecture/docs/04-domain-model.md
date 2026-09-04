# Доменна модель (витягнута з існуючого рішення)

Це «словник» предметної області: що таке ECR у термінах бізнесу, а не Excel.

---

## 1. Глосарій

| Термін | Що це в Excel зараз | Що це в новій системі |
|--------|---------------------|------------------------|
| **ECR** | Environmental Compliance Reporting — облік екологічних показників (вода, відходи, викиди, обладнання) | Домен |
| **RDS** | Reporting Data Sheet — набір даних по одному контракту | `doc.Document` у межах `doc.Project` |
| **Майстер-шаблон** | `RDS_..._Template_Land_2026.xlsm` | `Template` (версійований) |
| **HSE-файл** | Згенерована копія `.xlsm` для конкретного контракту | `doc.Document` |
| **Аркуш (Sheet)** | Worksheet | `SheetDef` — розділ шаблону |
| **Таблиця** | Блок рядків на аркуші, ідентифікований `templateRow` | `TableDef` |
| **Місяць** | Колонка (або блок рядків у Waste Report) | `doc.Period` (`PeriodKind`, `Sequence`, `StartDate`/`EndDate`) |
| **File Number** | `2. Contract!B16` | `doc.Document.BusinessKey` (поле `__Header` з `IsBusinessKey`) |
| **Permit** | Дозвіл на природокористування з датами дії | реєстр `Permit` (`RegistryDef`, `IsTemporal`, `Composition` -> речовини) |
| **Water body** | Водний об'єкт (море, річка, ставок-випарник) | реєстр `WaterBody` + `Association` з `Permit` |
| **Event Frame (EF)** | Запис у PI AF = один (документ × таблиця × місяць) | `doc.TableInstance` (документ × таблиця × період) |
| **Attribute_XXXX** | Один рядок таблиці, серіалізований у `;`-рядок | `doc.TableRow` (`RowKey`) + `CellValue[]`; мапінг — `ext.LegacyRowMapping` |
| **Location / Element** | Вузол дерева AF, до якого прив'язується EF | запис реєстру `Location` + `dic.RegistryExternalKey` (WebId) |
| **Amendment** | Зміна File Number з переносом/перевіркою існуючих EF | `TableRelationDef.OnSourceChange = Warn` на `MetadataSource`-зв'язку |
| **Cutoff day** | День місяця, після якого попередній місяць закривається (вводиться вручну щоразу) | `PeriodPolicy` з offsets: `OpenOffsetDays` / `GraceOffsetDays` / `HardCloseOffsetDays` |

---

## 2. Ієрархія сутностей

```
Template (шаблон, версія 1.0.4.0)
 └─ SheetDef  (напр. "9. Utility")
     └─ TableDef (Code = Utility_9_05; templateRow=241 і AF-шаблон — в ext.LegacyTableMapping, поза ядром)
         ├─ ColumnDef  (B..J = метадані, K..BF = місячні дані, BW..CC = додаткові)
         ├─ RowDef     (заздалегідь визначені рядки, або динамічні)
         └─ ValidationRule / FormulaDef

Project (рік, напр. "2026")           ← PeriodPolicy з offsets
 └─ Period[] (Scheduled | Open | Grace | Closed)
 └─ Document (BusinessKey = "test")     ← прив'язаний до TemplateVersion
     └─ TableInstance (TableDef × Period)
         ├─ Status: Draft | Submitted | Approved | Rejected
         └─ TableRow[] (RowKey — стабільний бізнес-ключ)
             └─ CellValue[] (ColumnDef, типізоване значення)
```

---

## 3. `2. Contract` — метадані документа

13 полів, які додаються **до кожного** запису в AF (`Action.GetContractSheetAttributes`):

| AF Attribute | Комірка | Приклад | Тип |
|--------------|---------|---------|-----|
| `Area` | B6 | `Island A - Острів А` | dropdown `Contract_Area` |
| `Contractor_Or_Company` | B7 | `AGS` | dropdown `Contract_Contractor` |
| `Region` | B8 | `Mangistau - Мангистау` | dropdown `Contract_Region` |
| `Location` | B9 | `Akku-1 barge - Баржа Акку-1` | dropdown `Contract_Location_Facility` |
| `Onshore_Offshore` | B10 | `Onshore - На суші` | dropdown |
| `Filled_In_By` | B11 | `TEST` | текст |
| `Contract_Holder` | B12 | `TEST1` | текст |
| `Contract_Number` | B13 | `321` | текст |
| `Type_Of_Activity` | B14 | `Operation - Експлуатація` | dropdown |
| `Processed_On` | B15 | `Version 2026.001 - 4.6.2026` | текст (парситься!) |
| `File_Number` | B16 | `test` | **бізнес-ключ**, зміна -> amendment flow |
| `Permit_Number` | B17 | `KZ06VCZ14825472` | dropdown -> резолвиться в `PermitId` (GUID) |
| `Version` | B18 | `1.0.0.0` | текст |

Важливо: `Permit_Number` **записується в AF як GUID `PermitId`**, а не як номер
(`Action.GetContractSheetAttributes` -> `PermitNumberToId`). Те саме для водних об'єктів
(`Name_water_body_Id`).

`B15` парситься формулою: `VALUE(TEXTBEFORE(TEXTAFTER(B15,"Version "),"."))` -> рік `2026`.

---

## 4. Типи рядків у `7. Water Report`

Колонка C містить тип рядка — це найпоказовіший приклад «таблиця з семантикою рядків»:

| Тип | Що це | Поведінка |
|-----|-------|-----------|
| `GROUP` | Група (напр. «Sea Water Intake») | рядок-підсумок, формула `SUM(I7:I11)`, заблокований |
| `ITEM` | Позиція (напр. «Service water package (m3)») | редагується, має permit і water body |
| `BALANCE` | Баланс | формула |
| `NOTE` | Примітка | текст |

Колонка B містить композит `"7001001 - Service water package (m3)"`; при експорті
береться лише ID (`Split(bRaw, " - ")(0)`).

> **У новій системі: `RowDef.RowType` (enum) + `TableDef` знає, які рядки обчислювані.**

---

## 5. Permits — вікна дії

`Configuration!AW:AZ` (синхронізується `PermitWaterSync`):

| Колонка | Поле |
|---------|------|
| AW | `PermitId` (GUID у AF) |
| AX | `Permit_Number` (напр. `KZ89VTE00309960`) |
| AY | `Start_Date` |
| AZ | `Actual_End_Date` |

Правило (`Sheet_Config.ApplyPermitMonthLocks`):
> Місяць редагується для рядка, якщо вікно дії дозволу **перетинається** з цим місяцем
> хоча б частково. Інакше місяць блокується, а при зміні дозволу — **очищається**.

Fail-open: якщо permit не знайдено або рік не резолвиться — місяць залишається відкритим.

`Configuration!BD:BL` — матриця `Permit × Water body` з типом
(`PermitWaterIntakeValue` / `PermitWaterDischargeValue`), керує каскадним dropdown-ом
колонки G у `7. Water Report`.

---

## 6. Звітний період і його закриття

Два незалежні механізми:

### 6.1 Межі проєкту (з AF)
```
SETTINGS\GENERAL\CALCULATION_STARTDATETIME  -> startYear
SETTINGS\GENERAL\CALCULATION_ENDDATETIME    -> endYear
```
`General.ValidateStartYearVsContractYear`:
* `contractYear < startYear` -> **заборона**
* `contractYear > endYear` -> **заборона**
* `contractYear < currentYear` -> підтвердження («минулий рік, продовжити?»)
* інакше -> дозвіл

### 6.2 Відкритий місяць (локально)
`Protection.ToggleProtection` + `GetEditableMonthName(cutoffDay, Date)`:
* до дня відсічення — відкритий попередній місяць;
* після — поточний.

**Проблеми чинної реалізації:**
* стан обчислюється в момент натискання кнопки, а не централізовано —
  у двох користувачів може бути різна картина;
* `cutoffDay` вводиться вручну щоразу у `frmPassword`, ніде не зберігається;
* відкритий рівно **один** місяць — немає поняття «пільгового періоду»;
* немає жодного сліду про те, що дані правили після закриття періоду.

### 6.3 Що буде в новій системі: стани + offsets

```
 Scheduled ──► Open ──────► Grace ──────► Closed ──► (Reopen адміністратором)
```

Межі станів задаються **зсувами (offsets)** відносно дат періоду,
які налаштовує адміністратор:

| Offset | Приклад ECR | Що означає |
|--------|-------------|------------|
| `OpenOffsetDays` | `0` | відкривається з першого дня періоду |
| **`GraceOffsetDays`** | **`+15`** | **ще 15 днів після завершення періоду дані редагуються** |
| `HardCloseOffsetDays` | `+30` | остаточне закриття |
| `YearGraceOffsetDays` (на проєкті) | `+45` | скільки після завершення **року** ще можна правити минулий рік |

Рівні: глобальна політика -> проєкт -> окремий період -> роль
(`ExtraGraceDays`). Виграє найконкретніший.

Зміни у стані `Grace` позначаються в аудиті прапорцем `IsLateEdit`,
опційно вимагають зазначення причини.

Переходи виконує фонова задача `PeriodStateJob` щодня — стан детермінований
і однаковий для всіх.

Детально — ТЗ ФВ-1.5…1.12.

---

## 7. Amendment flow (зміна File Number)

`Sheet1.cls` (`2. Contract`) ловить зміну `B16` -> `modB16AmendmentFlow.QueueB16AmendmentEFCheck`.

Далі асинхронно (`Application.OnTime`):
1. Побудувати каталог активних шаблонів по аркушах у книзі (`GetBSheetTemplateCatalog`);
2. Для кожного шаблону вивантажити EF за рік (`GetEFYearAndValuesService`);
3. Розкласти в дерево `FileNumber -> Template -> Month` для **старого** і **нового** номера;
4. Показати користувачу порівняння: що вже збережено під старим номером, що під новим.

Це фактично **ручний data-lineage-check**. У новій системі — повноцінна операція
«Змінити File Number» з транзакційним перенесенням або створенням нової версії документа.

---

## 8. Правила експорту значень комірок

`CellValueForExport` / `CellValueForExportPreserveDecimals` / `NumericCellTextForExport`:

| Стан комірки | Що експортується |
|--------------|------------------|
| Помилка (`#N/A`, `#REF!`) | `""` |
| Порожня | `"NULL"` |
| Число | плоский десятковий текст (без експоненти), кількість знаків береться з формату комірки |
| Об'єднана комірка | значення з верхньої лівої |
| Текст | as-is |

> **Це треба зберегти 1:1** — інакше нові дані стануть несумісні зі старими в AF.

---

## 9. Ролі, які фактично існують (неявно)

| Роль (де-факто) | Що робить зараз |
|-----------------|------------------|
| **Адміністратор шаблону** | Редагує майстер-шаблон, VBA, `Configuration` |
| **Оператор** | Генерує HSE-файл через `UserSelection`, заповнює, тисне Save |
| **Контролер періоду** | Знає пароль `1qazxcde3`, відкриває/закриває місяць через `ToggleProtection` |
| **PI-адміністратор** | Керує AF-шаблонами, довідниками, правами AD на запис |

> **У новій системі ці ролі формалізуються в RBAC.**

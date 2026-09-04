# 03 — Глосарій: термін → клас → таблиця

> Мета цього файлу — **однозначність**. Один термін = один клас = одна таблиця.
> Якщо в коді з'являється синонім («лист» замість «аркуш», `Sheet` замість
> `SheetDef`) — це помилка, а не стиль.

---

## 1. Структура звітності (метадані, схема `cfg`)

| Термін | Клас | Таблиця | Що це |
|---|---|---|---|
| Шаблон | `Template` | `cfg.Template` | іменований набір структур звітності; має версії |
| Версія шаблону | `TemplateVersion` | `cfg.TemplateVersion` | після `Publish` **структурно незмінна**; презентаційний шар патчиться з `PresentationRevision` |
| Аркуш | `SheetDef` | `cfg.SheetDef` | аналог аркуша Excel |
| Таблиця (визначення) | `TableDef` | `cfg.TableDef` | іменована таблиця на аркуші |
| Колонка (визначення) | `ColumnDef` | `cfg.ColumnDef` | поле: тип, формат, довідник, одиниця |
| Рядок (визначення) | `RowDef` | `cfg.RowDef` | для `RowMode = Fixed`; ідентичність — `RowKey` |
| Стиль | `StyleDef` | `cfg.StyleDef` | шрифт, заливка, межі, формат |
| Формула | `FormulaDef` | `cfg.FormulaDef` | вираз + область дії |
| Залежність формули | `FormulaDependency` | `cfg.FormulaDependency` | розкритий граф; діапазони матеріалізовані при `Publish` |
| Правило валідації | `ValidationRule` | `cfg.ValidationRule` | рівні `Info`/`Warning`/`Error` |
| Зв'язок таблиць | `TableRelationDef` | `cfg.TableRelationDef` | дзеркало / rollup / посилання / каскад / перевірка / копія |
| Правило доступу до періоду | `PeriodAccessRuleDef` | `cfg.PeriodAccessRuleDef` | заміна кнопки `Protect` |
| Правило складу документа | `SheetGroupRule` | `cfg.SheetGroupRule` | `RequiresAll` / `RequiresOne` / `Excludes` |
| Прив'язка розрахунку | `CalculationBinding` | `cfg.CalculationBinding` | результат методології → колонка документа |
| Визначення реєстру | `RegistryDef` | `cfg.RegistryDef` | схема довідника |
| Поле реєстру | `RegistryFieldDef` | `cfg.RegistryFieldDef` | |

**`RowKey`** — стабільний бізнес-ключ рядка (`"7001001"`). Для `RowMode = Dynamic`
генерується як `GUID` у форматі `"N"` (32 hex-символи без дефісів).
**`Ordinal`** — **тільки порядок відображення**. Єдине місце, де він бере участь
у логіці, — матеріалізація діапазонів формул на момент `Publish`.

## 2. Дані документів (схема `doc`)

| Термін | Клас | Таблиця | Що це |
|---|---|---|---|
| Проєкт | `Project` | `doc.Project` | звітний рік або інший діапазон |
| Політика періодів | `PeriodPolicy` | `doc.PeriodPolicy` | offsets |
| Період | `Period` | `doc.Period` | звітний інтервал; стан `Scheduled`/`Open`/`Grace`/`Closed` |
| Документ | `Document` | `doc.Document` | екземпляр звітності; аналог «HSE-файлу» |
| Екземпляр таблиці | `TableInstance` | `doc.TableInstance` | таблиця документа за період |
| Рядок | `TableRow` | `doc.TableRow` | `PK (PeriodKey, Id)`, `Id` із `SEQUENCE` |
| Комірка | `CellValue` | `doc.CellValue` | `PK (PeriodKey, TableRowId, ColumnDefId)` |
| Індексоване значення | `DocumentIndexValue` | `doc.DocumentIndexValue` | дублікат `IsIndexed`-полів для швидких фільтрів |

**`PeriodKey`** = `Year * 100 + Sequence`, де `Sequence` — номер періоду в році:
`Monthly` → 1…12, `Quarterly` → 1…4, `Yearly` → 1, `Custom` → порядковий номер.
Для місячних періодів збігається з `YYYYMM`. **Виводити місяць із `PeriodKey`
арифметикою заборонено** — місяць береться з `doc.Period`.

**Три різні операції над коміркою** (не плутати):
* `value: <значення>` — записати;
* `value: null` — **стерти**: рядок `CellValue` видаляється;
* `isEmpty: true` — **явна порожнеча**: рядок існує, усі `Value*` = `NULL`,
  `IsEmpty = 1` (аналог `"NULL"` у чинному експорті);
* **відсутність поля в запиті** — не чіпати.

## 3. Реєстри і одиниці (`dic`, `uom`)

| Термін | Клас | Таблиця |
|---|---|---|
| Запис реєстру | `RegistryEntry` | `dic.RegistryEntry` |
| Значення поля запису | `RegistryValue` | `dic.RegistryValue` |
| Зовнішній ключ запису | `RegistryExternalKey` | `dic.RegistryExternalKey` |
| Розмірність | `Dimension` | `uom.Dimension` |
| Одиниця | `Unit` | `uom.Unit` |
| Конверсія | `UnitConversion` | `uom.Conversion` |

**Конверсія** — перетворення **в межах однієї розмірності**. Щільність,
теплотворність, молярна маса — це **не конверсії**, а `calc.MethodologyConstant`
з власною одиницею: вони залежать від речовини й умов і змінюються з часом.

## 4. Розрахунки (схема `calc`)

| Термін | Клас | Таблиця |
|---|---|---|
| Методологія | `Methodology` | `calc.Methodology` |
| Версія методології | `MethodologyVersion` | `calc.MethodologyVersion` |
| Формула методології | `MethodologyFormula` | `calc.MethodologyFormula` |
| Константа | `MethodologyConstant` | `calc.MethodologyConstant` |
| Речовина методології | `MethodologySubstance` | `calc.MethodologySubstance` |
| Правило прив'язки | `MethodologyRule` | `calc.MethodologyRule` |
| Скрипт | `ScriptVersion` | `calc.ScriptVersion` |
| Прогін | `CalculationRun` | `calc.CalculationRun` |
| Результат | `CalculationResult` | `calc.CalculationResult` |
| Крок (трейс) | `CalculationStep` | `calc.CalculationStep` |

**Драбина виразності:** рівень 1 — конфігурація (формули як дані); рівень 2 —
скрипт C# як дані, компілюється при публікації; рівень 3 — модуль коду.

**Два різні місця обчислених значень** (не плутати):
* **формули шаблону** → `doc.CellValue` з `IsCalculated = 1`;
* **результати методологій** → `calc.CalculationResult`; у документ потрапляють
  **посиланням** через `ColumnDef.DataType = Calculated` + `cfg.CalculationBinding`.

## 5. Безпека (схема `sec`)

| Термін | Клас | Таблиця |
|---|---|---|
| Користувач | `User` | `sec.User` |
| Роль | `Role` | `sec.Role` |
| Функціональне право | `Permission` | `sec.Permission` |
| Право ролі | `RolePermission` | `sec.RolePermission` |
| Призначення ролі | `RoleAssignment` | `sec.RoleAssignment` |
| Ресурсний грант | `ResourceGrant` | `sec.ResourceGrant` |
| Політика паролів | `PasswordPolicy` | `sec.PasswordPolicy` |

**Рівні гранта:** `None` → `Read` → `Write` → `Submit` → `Approve` → `Manage`.
Успадкування `Project → Sheet → Table → Column`. **`IsDeny` виграє завжди.**

**`AccessProfile`** — не таблиця, а обчислений об'єкт: набір ефективних прав
користувача, побудований **раз на сесію** і закешований за ключем
`userId + templateVersionId + securityStamp`.

## 6. Робочий процес і аудит (`wf`, `aud`)

| Термін | Клас | Таблиця |
|---|---|---|
| Маршрут погодження | `ApprovalRoute` | `wf.ApprovalRoute` |
| Крок маршруту | `ApprovalStep` | `wf.ApprovalStep` |
| Стан затвердження | `ApprovalState` | `wf.ApprovalState` |
| Результат валідації | `ValidationResult` | `wf.ValidationResult` |
| Зміна комірки | `CellChange` | `aud.CellChange` |
| Зміна структури | `StructureChange` | `aud.StructureChange` |
| Подія безпеки | `SecurityEvent` | `aud.SecurityEvent` |
| Подія публікації | `PublicationEvent` | `aud.PublicationEvent` |
| Проблема консистентності | `ConsistencyIssue` | `aud.ConsistencyIssue` |

**Гранулярність затвердження — аркуш × період**, не документ.
В усіх таблицях аудиту автор — **`ChangedByUserId int → sec.User.Id`**, а не SID:
у локальних користувачів SID немає.

## 7. Інтеграція, звітність, архів (`ext`, `rpt`, `itg`, `arc`)

| Термін | Клас | Таблиця |
|---|---|---|
| Джерело даних | `DataSource` | `ext.DataSource` |
| Сутність джерела | `SourceEntity` | `ext.SourceEntity` |
| Мапінг поля | `EntityFieldMap` | `ext.EntityFieldMap` |
| Розклад збору | `CollectionSchedule` | `ext.CollectionSchedule` |
| Сира точка даних | `RawDataPoint` | `ext.RawDataPoint` |
| Мапінг legacy | `Legacy*Mapping` | `ext.Legacy*Mapping` |
| Зріз звіту | `ReportSnapshot` | `rpt.ReportSnapshot` |
| Рядок зрізу | `ReportRow` | `rpt.ReportRow` |
| Прогін збору | `CollectionRun` | `itg.CollectionRun` |
| Прогін архівації | `ArchiveRun` | `itg.ArchiveRun` |

**Уся специфіка PI AF і Excel** (`Af*`, `Legacy*`, `LegacyFieldIndex`,
`TemplateRow`) живе **тільки** в схемі `ext`. Ядро (`Ecr.Domain`,
`Ecr.Application`) про неї не знає — це перевіряється архітектурним тестом.

**`rpt.*`** — матеріалізований зріз **без логіки** і **постійний публічний
контракт** для SSRS. Зріз має статус `Draft` / `Approved` / `Submitted`;
регуляторні вʼюхи `rpt.v_*` віддають лише `Approved` і `Submitted`.

## 8. Терміни, які легко сплутати

| Не плутати | З чим | Різниця |
|---|---|---|
| `RowDef` | `TableRow` | визначення рядка в шаблоні / фактичний рядок у документі |
| `TableDef` | `TableInstance` | визначення таблиці / її екземпляр у документі за період |
| `PeriodKey` | `PeriodId` | детермінований ключ партиціонування / сурогатний Id запису |
| `Ordinal` | `RowKey` | порядок на екрані / ідентичність |
| `IsEmpty` | `NULL` | явна порожнеча (рядок є) / значення невідоме (рядка немає) |
| Конверсія одиниць | Константа методології | універсальна й контекстно-незалежна / залежить від речовини та умов |
| `Reopen` періоду | `Reopen` документа | `Closed` → `Grace` / поданий → `Draft` |
| Формула шаблону | Формула методології | діалект 11 Excel-функцій / NCalc-діалект, 24 функції |
| Стан періоду | `Project.CurrentPeriod` | керує доступом / лише значення за замовчуванням |

## 9. Мовна конвенція

Ідентифікатори — англійською, однина: `SheetDef`, не `SheetDefs` і не `Sheets`.
Колекції — множина: `IReadOnlyList<SheetDef> Sheets`.
XML-doc і коментарі — українською.
Скорочення розкриваються: `Def` = Definition, `L10n` = Localization,
`uom` = Unit of Measure, `cfg` = Configuration, `doc` = Documents,
`dic` = Dictionaries, `wf` = Workflow, `sec` = Security, `aud` = Audit,
`itg` = Integration, `arc` = Archive, `ext` = External, `rpt` = Reporting,
`calc` = Calculations.

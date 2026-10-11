# Land: модель шаблону RDS_xxx_Template_Land_2026_v1.0.4.0 для побудови в ECR

Каталог містить машиночитні дані, з яких `tools/land/Build-LandTemplate.ps1` будує в ECR повний шаблон Land
(усі звітні аркуші Excel-шаблону) і базу для UI-приймальної.

| Файл | Що це |
|---|---|
| `land-template.model.json` | модель побудови: 20 аркушів, 83 таблиці, 5 145 колонок (з них 4 977 логічних колонок Excel), довідники, рядки Fixed-таблиць, правила валідації, мостові формули, виходи методологій |
| `af-land-package.json` | пакет методологій PI AF (`ecr-methodology-package` v1): Common + ECW_C05_01..05, C06_01, C07_07, C07_07_01, C07_08, C07_16, C07_21 (з `tools/Ecr.MethodologyImport export`, без блокерів) |

Джерело — `docs/source/ECR Current Front end/RDS_xxx_Template_Land_2026_v1.0.4.0 2.xlsm` (FE) і
`docs/source/ECR Current Back end/Excel/RDS_xxx_Template_Land_2026_v1.0.4.0.xlsm` (BE); модель побудована з FE.

## Як пересоздати модель з xlsm

```powershell
py -3.12 -m pip install openpyxl
.\tools\land\model-gen\run-model-gen.ps1 `
    -Front "docs\source\ECR Current Front end\RDS_xxx_Template_Land_2026_v1.0.4.0 2.xlsm" `
    -Back  "docs\source\ECR Current Back end\Excel\RDS_xxx_Template_Land_2026_v1.0.4.0.xlsm"
```

## Як побудувати базу

```powershell
# 1. нова порожня база (SmallFiles — для стенда)
powershell -File tools\setup-dev-db.ps1 -Database EcrLandUi -Documents 0 -BootstrapPassword <разовий> -SmallFiles -DataPath F:\EcrData
# 2. API на порту 5094 (змінні: ECR_ConnectionStrings__Ecr, ECR_Bootstrap__Password, ASPNETCORE_URLS)
# 3. побудова (ідемпотентно; повторний запуск = 0 змін)
$pw = Read-Host 'Пароль bootstrap' -AsSecureString
.\tools\land\Build-LandTemplate.ps1 -BaseUrl http://localhost:5094 -BootstrapPassword $pw -Stage All
```

Етапи: `Accounts, Units, Registries, Contract, Template, Project, Documents, Methodologies, Data` (див. `-Stage`).
Паролі тестових акаунтів генеруються й пишуться в `-AccountsFile` (за замовчуванням поза репозиторієм).

## Правила відображення Excel -> ECR (скорочено)

* Excel-аркуш -> `SheetDef`; блок із заголовком `Status/Работает` (або `SubstanceId`) -> `TableDef`; «2. Contract» -> поля шапки
  (`Apply-ContractHeader.ps1`, 13 полів); `Configuration` і `DropdownList` -> довідники (`LAND_*`); «1. Sheet Selection» -> вибір аркушів
  при створенні документа (`sheetDefIds`).
* Колонка Excel -> `ColumnDef`; місячні групи -> `IsMonthColumn`/`MonthNumber`; DV `list` -> `Lookup` на довідник; DV `decimal/whole` ->
  правило валідації `DV_<колонка>` (Error); службові блоки Promasys/EANS (Tags/Attributes) -> приховані колонки.
* Таблиці з методологіями (аркуші 3, 4, 5) — `PerPeriodInstance`; мостові Formula-колонки вибирають значення місяця за `[Period].Sequence`
  (колонки `Land_*` без суфікса `_Mxx`), далі працюють методології AF. Решта таблиць із місяцями — `MonthsInColumns`.
* Структура заливається масово SQL (`-StructureMode Sql`, за замовчуванням): PUT колонки через API вантажить усю версію на кожен виклик
  (O(n)), для 5 тис. колонок це години. Гілка `-StructureMode Api` лишилась для малих шаблонів.

Перелік прогалин і пропозицій по змінах коду — у звіті `.sync-local/reports/LAND-TEMPLATE-BUILD-2026-10-11.md`.

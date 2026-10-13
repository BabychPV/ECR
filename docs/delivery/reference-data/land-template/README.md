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

## Стилі комірок (ролі Excel, L1)

Ключі моделі, які генерує `run-model-gen.ps1` зі `styles.xml` xlsm (кольори тільки з книги; теми й indexed-палітра переведені в RGB):

* `styles[]` — ролі: `LAND_HEADER` (CCFFCC), `LAND_HEADER_MONTH` (FFCC99), `LAND_INPUT` (FFFFFF), `LAND_AUTO` (CCFFCC), `LAND_SERVICE` (FFFF00),
  `LAND_TAG` (FFFF99) та похідні (`_BOLD`, `_RED`, `LAND_FILL_<HEX>`); поля = `SaveStyleDefCommand` (ARGB — знакове int).
* колонка: `styleRole`, `headerStyleRole`, `widthPx` (40..800; px = `<col width>` x 7), `displayFormat` (лише числові колонки, <= 50 символів);
  таблиця: `headerStyleRole` (мода шапок немісячних колонок). Роль колонки ставиться, лише коли найчастіший xf охоплює >= 80 % її рядків.
* `conditionalFormats` лишається порожнім. Правила Excel «баланс <> 0 -> FF0000 / = 0 -> 00FF00» і «Removed -> червоний жирний» стоять на окремих
  рядках, а правило колонки ECR діє на всі рядки колонки в усіх таблицях версії, тому вони лежать у `conditionalFormatsRowScoped`
  (рядки за `rowKeys`, колонки за кодами). `Build-LandTemplate.ps1` перетворює їх (`tools/land/Land-CfRules.ps1`) у правила з областю
  (`scope = row`, `tableCode`, `rowKeys`, `textEq`) і PUT-ить за контрактом L6 (lane cellstyle-l6-3); без неї відкочує до колонкових.

`Build-LandTemplate.ps1` ставить стилі через `PUT .../styles/{code}`, `styleId`/`widthPx`/`displayFormat` — у колонках (обидва режими), а стиль шапки
таблиці — `cfg.TableDef.HeaderStyleId` лише в `-StructureMode Sql` (у API-контракті таблиці поля немає; до сітки шапка дійде з L2 плану).

Перелік прогалин і пропозицій по змінах коду — у звіті `.sync-local/reports/LAND-TEMPLATE-BUILD-2026-10-11.md`.
## Відповідність прод-колонкам HSE (prodColumnMapping)

Зіставлення зі зразком прод-БД HSE (структура, без даних; звіт `.sync-local/reports/LAND-PROD-SAMPLE-2026-10-12.md`):
ключ `prodColumnMapping` моделі — аліаси прод-колонок AF (`Land_Measure_*`, `Land_EF_*`, `Land_AL_*`, `Land_Agent_*` …)
на колонки шаблону за заголовком Excel (`evidence`: header / single / position), текстові мости `*Ref`, 55 доданих колонок
введення (`Land_TypeFuel` у 10.01/10.03/10.04/11.01, `Land_ProcessTrain` у 11.03, `Land_ERG_EmissionSourceType` у 5.08/5.09/5.27,
`Land_Measure_GTU*` у 10.03/11.03), `unitMismatches` і `notProven`. Перейменувань наявних колонок немає. Джерело мапи —
`tools/land/model-gen/prod_mapping.py` (генерація падає, якщо заголовок не знайдено або неоднозначний).

## Колонки результатів методологій C08/C09/C11/C12 (LP-08)

До цього колонки `ECW_EC_*` мали лише таблиці аркушів 3, 4, 5 (`CALC` генератора). Для решти їх бракувало, тож вихід
опублікованих методологій C08/C11/C12 не мав куди прив'язатись. Тепер генератор читає `land-result-outputs.json`
(складає `tools/land/model-gen/extract_result_outputs.py`: імена формул `ECW_EC_tons_*` / `ECW_EC_gsec_*` пакета
методологій AF x таблиці правил прода; значень констант і даних у файлі немає) і додає в таблицю колонки результатів тієї
самої форми, що й на аркушах 3-5: `Calculated`, лише читання, 28/12, `t` (tons) або `g/s` (gsec), код колонки = код
виходу, `ecrAdded` = `methodology output (CalculationBinding)`. Ключ таблиці `calc` = `{methodologies, outputs}`;
`resultOutputsPending` — таблиці зі зіставлення, яких у шаблоні немає (після LP-09 порожній).
Виходи, що не є `tons`/`gsec` (проміжні `ECW_EC_SumXiDivMi/Ri` у C11_11_02), колонок не мають (`excludedOutputs`).
`Build-LandTemplate.ps1` створює колонки в обох режимах (Sql/Api) разом зі структурою, а `Bind-ResultColumns` (етап
Methodologies) PUT-ить `/methodologies/{m}/bindings/{colId}/{output}` для методологій, що вже є на сервері (імпорт —
`tools/Ecr.MethodologyImport` / `POST /methodologies/import`, поза цим скриптом); прив'язка активна лише для
опублікованої останньої версії. Перегенерація: `extract_result_outputs.py` (при зміні пакета методологій або правил), далі
`run-model-gen.ps1`.

## Таблиці 9a.17а, 9a.21a, 9a.21b і 7b (LP-09)

Ці таблиці в шаблоні були завжди, але під хибними кодами: заголовки Excel «9a.17а» (кирилична а), «9a.21a» (двічі) не
розпізнавались `sec_of` у `gen_ecr_model.py` (вимагав межу слова одразу після цифр), і код брався з порядкового номера
блоку: `T9a_03`, `T9a_06`, `T9a_07`. Тепер `T9a_17a`, `T9a_21a`, `T9a_21b` (другий блок із заголовком «9a.21a» =
прод `AF_Air_Land_ProcessUnits_9a_21b`: позиція між 9a.21a і 9a.22, 122 фізичні колонки Excel, як у 9a.22, і ті самі
методології). Колонки, типи, рядки не змінено, лише код таблиці (і ключ у правилах умовного форматування за рядками).
⚠ Версія шаблону зі старими кодами після міграції втратить дані цих трьох таблиць (код таблиці = ідентичність): на стенді
вони порожні (прод-даних для 9a немає). 7b: єдина таблиця аркуша 7b має код `T7b_00`; скрипт завантаження рулів мусить
мапити `AF_Water_Land_07b` на `T7b_00` (`T7b` у шаблоні немає).

## Мости аргументів методологій для аркушів 6, 9-11 (LP-10)

Таблиці 6, 9, 9a, 10, 10a, 11, 11a-d мають розкладку MonthsInColumns і не мали Formula-мостів, які на аркушах 3-5 дають
методології аргумент за кодом (`X` = вибір `X_M01..X_M12` за `[Period].Sequence`). Генератор (`_arg_bridges` у
`gen_ecr_model.py`) додає лише доведене: (1) alias прод-колонки AF із `prodColumnMapping` (`monthColumns` -> місячний міст,
`column` числова -> `[колонка]`) для входу, який вживає методологія таблиці (`inputs` у `land-result-outputs.json`);
(2) календарні аргументи `CalculationMonth`, `DaysInCalcYear`, `Winter` тими ж формулами, що в EXTRA таблиць 3-5.
Усе інше — ключ моделі `argumentBridges.unresolved` (таблиця -> вхід -> причина), без мостів: `CompositionMass` (склад газу
по речовині), `DaysInCalcMonth` (правило 7.7 доведено лише для 3-5), `Land_Measure_OperatingTime` та ін.

Склад газу HSE400 (`Density20`, `Density0`, `Density`, `Vddg`, `Vwdg`, `TotalSulphurContent`, `MercaptanSulphurContent`,
`FuelLowerHeating`, `H2Smass`) мостить `model-gen/hse400_bridges.py` (LP-10): довідники `LAND_HSE400_STREAM` (текст палива ->
префікс потоку) і `LAND_HSE400_COMPOSITION` (рік, місяць, потік, показник -> значення; у моделі порожній, значення вантажить
`tools/land/Import-LandHse400Composition.ps1` з локальної копії прода) і Formula-міст
`REGFIELD(REGFIND('LAND_HSE400_COMPOSITION', [Period].Year, [Period].Sequence, <потік>, '<показник>'), 'VALUE')`. Колонка-
джерело потоку - лише однозначна (`TypeOfFuel` аркуша 9; `Land_TypeFuel` у 10_01/11_01): ключ `argumentBridges.hse400`.
Немає запису за період/потік/показник - `#N/A`, рядок без результатів (стара система: `continue`). Одиниці мостів не
задані (старі одиниці в FInfo/AIR.xml не звірено).

`CompositionMass` (склад по речовині) колонкою-мостом бути не може (діалект шаблонів не має `CURRENT_SUBSTANCE()`):
`{код}` у `{код}_mass` - числовий код речовини (SUBSTANCE_CODE), а колонка прода названа хімічно (`H2S_mass`,
`C1_C5_mass` ...), тож зіставлення - таблиця старого `PollutionCodeService.GetMassName` (15 кодів) = довідник
`LAND_HSE400_SUBSTANCE` (ключ SUBSTANCE_CODE; код запису `C<код>`, не `E<код>` довідника речовин). У формулі версії
методології `@CompositionMass` замінюється фрагментом `argumentBridges.hse400.compositionMass.methodologies.<ім'я>.fragment`
(`Build-LandTemplate.ps1 -Stage CompositionMass`; довідник - `-Stage Registries` або `Import-LandHse400Substance.ps1`).

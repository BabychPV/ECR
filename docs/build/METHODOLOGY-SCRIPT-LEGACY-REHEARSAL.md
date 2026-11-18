# Методології як скрипт: генеральна репетиція етапу 8 на копії прода старої системи (18.11.2026)

План: `docs/build/METHODOLOGY-SCRIPT-PLAN.md`, розд. 8 і етап 8. Гілка `lane/ms-r-legacy` (злито `lane/ms-e2e-fix`, коміт 5588863a0; усі цифри нижче — на злитому коді).
Джерело: локальна копія бази старої системи `EcrLegacyProd` (лише читання). Нова система для репетиції: `EcrLandMsLegacy` (назва з префіксом `EcrLand*`, бо так вимагає `Build-LandTemplate.ps1`), API на порту 5094.
У документ потрапили **лише агреговані цифри й коди методологій**; значень, імен, коментарів і облікових записів прода тут немає.

## 1. Що в старій системі і що вміє імпортувати нова

У `EcrLegacyProd`: 49 методологій (кожна з однією версією `V1`), 2010 формул, 6507 значень констант (1125 імен), 348 правил `AF_EmissionCalculationWork_Rules`, 2384 записи `AF_ArgumentsUniqueInternal`.

**До цієї репетиції шляху «дзеркала SQL → методології нової системи» не було:** `tools/Ecr.MethodologyImport` читав лише AF XML, пакет `ecr-methodology-package` потрапляє в базу через `POST /api/v1/methodologies/import`. `tools/Ecr.Migration.PiAf` — заглушка (`NotImplementedException`), рядків старої системи не переносить.

Реалізовано (коміт `61badd417`): джерело `--sql` у `tools/Ecr.MethodologyImport` (`Reading/LegacyMirrorReader.cs`, `LegacySqlMirrorSource.cs`). `AF_Formulas` і `AF_Constants` читаються (`SELECT … WITH (NOLOCK)`, намір ReadOnly) у ту саму модель, що й XML; далі ті самі аналіз, нормалізація, пакет. Локальні дати дзеркала переводяться в UTC-рядки AF за поясом майданчика (`--timezone`, за замовчуванням `Asia/Atyrau`; збігається з датами пакета `af-land-package.json`). Тести на синтетичних даних: `LegacyMirrorReaderTests` (пакет із дзеркал дорівнює пакету з XML для тих самих даних, дати, Trim, фільтр, недоступні формули, блокери).

Знайдений дефект: правила нормалізації `ReferenceNormalizer` (`CST.Mh2s_ECW_C11_11_02_` → `MMh2s…`, `k3_ECW_C11_11_05_` → без «_») написані під назви в AF XML; у дзеркалі ці константи названі так, як на них посилаються формули, і «виправлення» створювало 5 нерезолвних посилань. Тепер правило не спрацьовує, якщо ім'я-джерело визначене в моделі (тест у `ReferenceNormalizerTests`).

Результат на всіх 49: **блокерів 0, нерезолвних посилань 0, циклів 0**; пакет: 49 методологій, 2010 формул, 1125 констант. Імпорт у `EcrLandMsLegacy` через API: `dry-run` і `import` — `outcome=created`, 49 з 49.

Правила рядків (`AF_EmissionCalculationWork_Rules`, прив'язки таблиць `RuleArg_TableLandFromDB`) **жоден інструмент не імпортує**: пакет формату v1 їх не має. У репетиції правила й прив'язки створив `Build-LandTemplate.ps1` зі своїх моделей лише для 9 методологій Land (C05_01..05, C06_01, C07_07, C07_08, C07_21), і саме вони опубліковані (золоті тести пройшли на константах прода). Решта 40 лишилися чернетками (прив'язки до таблиць дають `ECR-CALC-0422`, правил рядків немає).

## 2. Таблиця за методологіями

Колонки: код; група; формул; значень констант (імен); правил у старій системі; імпорт; `convert`; паритет формули↔скрипт «рядків / значень / розбіжностей»; звірка зі старою системою «значень збіг / NEAR з відносною похибкою понад 1e-9 / решта (див. розд. 5)». Групи: Water — `ECW_C09`; методологій Waste у старій системі немає; HSE — формули факелів, HSE400, термоокисник.

| Код | Група | Формул | Значень констант (імен) | Правил | Імпорт | convert | Паритет (рядків / значень / розбіжностей) | Звірка зі старою (збіг / NEAR / решта) |
|---|---|---|---|---|---|---|---|---|
| Common | Бібліотека | 49 | 281 (275) | 0 | так | ні: чернетка | — | — |
| ECW_C05_01 | Air | 29 | 330 (29) | 6 | так | так | 16388 / 294984 / 0 | 134980 / 44 / 16878 |
| ECW_C05_02 | Air | 18 | 30 (16) | 2 | так | так | 4942 / 59304 / 0 | 24703 / 7 / 4942 |
| ECW_C05_03 | Air | 17 | 12 (12) | 1 | так | так | 0 / 0 / 0 (правило не відібрало рядків) | 0 / 0 / 0 |
| ECW_C05_04 | Air | 10 | 36 (18) | 6 | так | так | 1250 / 12500 / 0 | 6249 / 1 / 0 |
| ECW_C05_05 | Air | 8 | 15 (15) | 10 | так | так | 224 / 0 / 0 (значень немає: ввід неповний в обох рушіях) | 0 / 0 / 896 |
| ECW_C06_01 | Air | 32 | 27 (9) | 2 | так | так | 240 / 3840 / 0 | 1490 / 6 / 1192 |
| ECW_C07_07 | Air | 44 | 69 (20) | 7 | так | так | 676 / 4600 / 0 | 1108 / 0 / 1144 |
| ECW_C07_07_01 | Air | 6 | 27 (14) | 2 | так | ні: чернетка | — | — |
| ECW_C07_08 | Air | 80 | 70 (27) | 8 | так | так | 42 / 348 / 0 | 84 / 0 / 90 |
| ECW_C07_16 | Air | 13 | 18 (10) | 1 | так | ні: чернетка | — | — |
| ECW_C07_21 | Air | 8 | 47 (17) | 4 | так | так | 28 / 168 / 0 | 84 / 0 / 0 |
| ECW_C08_04_02 | Air | 3 | 18 (15) | 1 | так | ні: чернетка | — | — |
| ECW_C08_04_03 | Air | 4 | 4 (4) | 2 | так | ні: чернетка | — | — |
| ECW_C09_02_01 | Water | 1 | 1 (1) | 17 | так | ні: чернетка | — | — |
| ECW_C11_01 | Air | 12 | 858 (13) | 24 | так | ні: чернетка | — | — |
| ECW_C11_02 | Air | 6 | 273 (21) | 10 | так | ні: чернетка | — | — |
| ECW_C11_03 | Air | 26 | 80 (20) | 15 | так | ні: чернетка | — | — |
| ECW_C11_04 | Air | 15 | 40 (20) | 6 | так | ні: чернетка | — | — |
| ECW_C11_05 | Air | 18 | 69 (23) | 3 | так | ні: чернетка | — | — |
| ECW_C11_06 | Air | 10 | 20 (20) | 4 | так | ні: чернетка | — | — |
| ECW_C11_07 | Air | 15 | 57 (19) | 7 | так | ні: чернетка | — | — |
| ECW_C11_08 | Air | 12 | 64 (16) | 31 | так | ні: чернетка | — | — |
| ECW_C11_10 | Air | 10 | 106 (7) | 51 | так | ні: чернетка | — | — |
| ECW_C11_11_01 | Air | 12 | 139 (15) | 18 | так | ні: чернетка | — | — |
| ECW_C11_11_02 | Air | 34 | 297 (45) | 24 | так | ні: чернетка | — | — |
| ECW_C11_11_03 | Air | 7 | 802 (16) | 17 | так | ні: чернетка | — | — |
| ECW_C11_11_04 | Air | 6 | 48 (16) | 3 | так | ні: чернетка | — | — |
| ECW_C11_11_05 | Air | 4 | 21 (5) | 5 | так | ні: чернетка | — | — |
| ECW_C11_12_01 | Air | 3 | 208 (13) | 2 | так | ні: чернетка | — | — |
| ECW_C11_12_03 | Air | 3 | 12 (6) | 2 | так | ні: чернетка | — | — |
| ECW_C11_13 | Air | 16 | 33 (6) | 7 | так | ні: чернетка | — | — |
| ECW_C11_14 | Air | 2 | 8 (4) | 2 | так | ні: чернетка | — | — |
| ECW_C11_15 | Air | 6 | 11 (11) | 1 | так | ні: чернетка | — | — |
| ECW_C11_16 | Air | 6 | 63 (18) | 4 | так | ні: чернетка | — | — |
| ECW_C11_17 | Air | 3 | 9 (9) | 1 | так | ні: чернетка | — | — |
| ECW_C11_18 | Air | 3 | 36 (12) | 3 | так | ні: чернетка | — | — |
| ECW_C11_19 | Air | 3 | 114 (6) | 7 | так | ні: чернетка | — | — |
| ECW_C11_20_01 | Air | 3 | 36 (9) | 1 | так | ні: чернетка | — | — |
| ECW_C11_20_02 | Air | 3 | 28 (7) | 1 | так | ні: чернетка | — | — |
| ECW_C12_01 | Air | 6 | 615 (10) | 5 | так | ні: чернетка | — | — |
| ECW_C12_02 | Air | 20 | 144 (22) | 2 | так | ні: чернетка | — | — |
| ECW_C12_03 | Air | 3 | 5 (5) | 2 | так | ні: чернетка | — | — |
| ECW_C12_04_01 | Air | 3 | 396 (18) | 6 | так | ні: чернетка | — | — |
| ECW_C12_04_02 | Air | 4 | 36 (12) | 3 | так | ні: чернетка | — | — |
| ECW_C12_05 | Air | 26 | 80 (20) | 2 | так | ні: чернетка | — | — |
| Flert | HSE/факели | 407 | 603 (129) | 0 | так | ні: чернетка | — | — |
| HSE400 | HSE/факели | 341 | 23 (23) | 0 | так | ні: чернетка | — | — |
| Thermaloxidizer | HSE/факели | 640 | 188 (47) | 0 | так | ні: чернетка | — | — |

Разом `convert`: перетворено **9, відмов 0, попереджень 0**; пропущено 40 (чернетки без дати чинності: `draft without EffectiveFrom`). `Common` — бібліотека, не опублікована. `switch` на Script виконано для 8 з 9 версій (C05_05 — відмова, розд. 3).

## 3. Паритет формули ↔ скрипт (`parity`, злитий код)

`shadow` записано 9 з 9; `parity` без фільтра: **екземплярів таблиць 252, рядків 23 790, значень 375 744, розбіжностей 0** (`isEqual`, `isProven`). Документи: 4 документи `LAND-PROD-1..4`, 7 періодів (2026-01…07), реальні входи (розд. 4).

Окремо за методологією (значень): C05_01 294 984, C05_02 59 304, C05_04 12 500, C06_01 3 840, C07_07 4 600, C07_08 348, C07_21 168. Жодного значення не порівняно: C05_03 (правило рядків не відібрало жодного рядка в реальних даних) і C05_05 (224 рядки відібрано, значень немає: обидва рушії однаково не мають вхідних аргументів; порівнюються множини відсутніх аргументів — збіг).

`importLibraryVersionsDiffer` (нова Error після злиття): **не спрацював на жодній реальній методології** — конвертовані версії не мають складу бібліотек, що різнився б між періодами. Увага: сама `Common` у цій репетиції лишилась чернеткою, тому перевірку на опублікованій бібліотеці прода ще не пройдено.

### Розбіжності й відмови: класифікація

| № | Що | Клас | Деталі |
|---|---|---|---|
| 1 | `switch` відмовляв `switch.problem.outputsDiffer` для всіх 9 версій | **(а) дефект перемикання**, виправлено | Сховище версій, створених збирачем/API, тримає `IsPerSubstance = true` у виходів версій **без речовин**, а скрипт без `foreach substance` цієї ознаки не має. Для версії без речовин ознака не змінює жодного числа. `ScriptMaterializer.DiffAgainstStoredAsync` тепер порівнює її лише якщо в версії є речовини (тест `Switch_версії_без_речовин_ігнорує_ознаку_по_речовинах_у_збереженому_виході`). Це зачепило б кожну версію прода, створену `Build-LandTemplate.ps1` |
| 2 | `ECW_C05_05` не перемикається: `script.unitMismatch` (`expr.unit.dimensionMismatch`) для `ECW_EC_gsec_330/337`, `ECW_EC_tons_330/337` | (б) дані / (в) обмеження плану | Перевірка одиниць скрипта (план T6.4). Константи в дзеркалі без одиниць (`CInfo_Unit` порожній), вихід заявлено в g/s і t; у формульному шляху така перевірка не діє. Потрібне рішення методолога (одиниці констант). Версія лишилась на `Formulas`, її паритет значень порожній (див. вище) |
| 3 | 40 версій не конвертуються | (в) свідоме обмеження плану 8.1/8.2 | Версії рівня Configuration з `Engine = Formulas` і датою чинності; прив'язок і правил рядків для них у новій системі немає (розд. 1) |

Розбіжностей паритету формула↔скрипт, що потребували б правки рушія чи конвертера, **не знайдено**.

## 4. Реальні входи

`tools/land/Export-LandProdSample.ps1` (новий, лише читання, вихід поза репозиторієм) експортує `AF_Air_Land_*` у `LAND-PROD-n.json`; EFID (подія = один звіт об'єкта за місяць) пакуються в документи першим вільним, ємність таблиці-місяця 900 рядків. `tools/land/New-LandProdDocuments.ps1` (новий) створює документи. Завантаження — штатним `Load-LandRows.ps1`, лише таблиці з опублікованими методологіями: T3_00, T4_01, T5_08, T5_09, T5_27.

- 4 документи, 7 періодів, 24 086 рядків до завантаження, **23 932 завантажено, 154 відхилено** (`ECR-ROW-0409`, T5_08 у LAND-PROD-1: січень 54, лютий…червень по 20): ліміт рядків T5_08 у шаблоні нижчий за 900 (ємність експортера має бути своєю для кожної таблиці). У звірці ці рядки — `ROW_NOT_LOADED_CAPPED`;
- не завантажено: решта 37 таблиць `AF_Air_Land_*` (немає опублікованих методологій), `AF_Water_*` і `AF_Waste_*` (завантажувач не підтримує: ключі за елементами шаблону), HSE400 (`AF_AirGasCompositions_HSE400`, 9 рядків) і факели — потрібен окремий мапінг колонок на документ HSE400;
- перерахунок: 28 задач на формульному шляху (всі `Succeeded`), 28 — на скриптовому.

## 5. Звірка зі старою системою (`CalcResult_*`, `Compare-LandParityFull.ps1 -AggregateBy NaePhase`)

Еталон: 222 750 збережених результатів `CalcResult_Emissions_Air_Land*` за 2026 (`Export-LandParityOracleFull.ps1 -Database EcrLegacyProd`). Допуск: MATCH ≤ 5e-13 абсолютна; NEAR ≤ 1e-9 (README `tools/land`).

**Формульний і скриптовий шлях дали ідентичний результат: файл `cells.tsv` збігається побайтно (83 694 не-MATCH клітинки), класи й кількості однакові. Розбіжностей скриптового шляху, яких немає у формульного, 0.** Клітинок усього 248 022.

| Клас | Клітинок | Причина |
|---|---|---|
| `AGG_MATCH` + `MATCH` | 155 302 + 9 026 | збіг |
| `AGG_MATCH_ROUNDING_LOSS` + `MATCH_ROUNDING_LOSS` | 4 083 + 158 | абсолютна різниця ≤ 5e-13, відносна понад 1e-9: значення біля нуля; історичний поріг збереження (README `tools/land`, `StorageAbs`). Окремо клітинки не аналізовано |
| `AGG_NEAR` | 187 (C05_01 66, C05_04 99, C06_01 15, C05_02 7) | абсолютна різниця ≤ 1e-9, найбільша відносна 2,1e-4 (медіана 2,3e-10). Узгоджується з точністю збереження старої системи; причину по клітинках не перевіряв |
| `AGG_ECR_ONLY_INTERMEDIATE`, `ECR_ONLY_INTERMEDIATE` | 20 672 + 1 148 | ECR пише проміжні виходи (`ECW_EC_*_NOx` тощо), яких стара система не зберігає |
| `AGG_ROW_NOT_LOADED_NO_CELLS` (+`_ZERO`) | 10 006 (+38 546) | рядки таблиць, які не завантажувались (розд. 4) |
| `AGG_ROW_NOT_LOADED_CAPPED` (+`_ZERO`), `AGG_INCOMPLETE_ROW_NOT_LOADED_CAPPED` | 194 (+24), 50 | ліміт рядків T5_08 (розд. 4); C07_07 |
| `AGG_LEGACY_STATUS_SKIP` | 5 276 (C12_03 3276, C12_02 840, C06_01 768, C12_04_01 392) | стара система пропускає не-`Running` рядки; C12 — методології-чернетки |
| `AGG_LEGACY_COPY_COUNT_DIFF` | 1 064 (лише C12_01) | кількість копій результату в старій системі не дорівнює кількості Running-рядків; методологія-чернетка |
| `AGG_MISSING_BOUND_NO_RESULT` (+`_ZERO`) | 536 (+360) | C05_05: ECR результату не дав (рядки відібрано, аргументи `Density`, `FuelLowerHeating`, `H2Smass`, `MercaptanSulphurContent` відсутні в завантажених таблицях; джерело їх у старій системі не з'ясовував) |
| `AGG_ECR_ONLY_ZERO` | 1 390 | ECR дає нулі там, де запису старої немає |

Методології C08, C09, C11, C12, `Flert`, `Thermaloxidizer` у ECR не опубліковані, тож усі їхні клітинки — лише в еталоні (`oracleOnly`/`ROW_NOT_LOADED`): числове порівняння для них неможливе.

## 6. Відтворення

Передумови: SQL Server на `localhost` (Windows-автентифікація), .NET 10, `dotnet restore Ecr.sln`; `$sp` — тека поза репозиторієм.

```powershell
$cs = 'Server=localhost;Database=EcrLegacyProd;Integrated Security=true;TrustServerCertificate=true'
# 1. Пакет методологій із дзеркал SQL старої системи
dotnet build tools\Ecr.MethodologyImport
dotnet tools\Ecr.MethodologyImport\bin\Debug\net10.0\Ecr.MethodologyImport.dll export --sql $cs --out $sp\legacy-package.json
# 2. База нової системи й API (в іншому вікні; наприкінці зупинити)
powershell -File tools\setup-dev-db.ps1 -Server localhost -Database EcrLandMsLegacy -Documents 0 -SmallFiles -DataPath F:\ecr-wt\upgdata-ms
$env:ECR_ConnectionStrings__Ecr = 'Server=localhost;Database=EcrLandMsLegacy;Trusted_Connection=True;TrustServerCertificate=True'
$env:ECR_Bootstrap__Password = 'Dev-Bootstrap-2026!'; $env:ASPNETCORE_URLS = 'http://localhost:5094'; $env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet run --project src\Ecr.Api --no-build --no-launch-profile
# 3. Шаблон Land, проєкт, документи, імпорт усіх 49 методологій, публікація 9 Land
#    ($bp — SecureString bootstrap-пароля; запускати в тому ж сеансі через &)
$sql = 'Server=localhost;Database=EcrLandMsLegacy;Integrated Security=true;TrustServerCertificate=true'
foreach ($st in 'Accounts,Units,Registries,Contract','Template','Project,Documents','Methodologies') {
  & .\tools\land\Build-LandTemplate.ps1 -BaseUrl http://localhost:5094 -SqlConnection $sql -ConfirmStand localhost:5094/EcrLandMsLegacy `
    -AccountsFile $sp\land-accounts.txt -AfPackage $sp\legacy-package.json -BootstrapPassword $bp -Stage ($st -split ',') }
# 4. Реальні входи ($ownerPw — SecureString пароля land-owner з $sp\land-accounts.txt)
& .\tools\land\Export-LandProdSample.ps1 -OutDir $sp\sample
& .\tools\land\New-LandProdDocuments.ps1 -Count 4 -BaseUrl http://localhost:5094 -ConfirmStand localhost:5094/EcrLandMsLegacy -SqlConnection $sql -User land-owner -Password $ownerPw
$map = '{"LAND-PROD-1":3,"LAND-PROD-2":4,"LAND-PROD-3":5,"LAND-PROD-4":6}'
& .\tools\land\Load-LandRows.ps1 -SourcePath $sp\sample -DocMap $map -BaseUrl http://localhost:5094 -ConfirmStand localhost:5094/EcrLandMsLegacy `
    -SqlConnection $sql -User land-owner -Password $ownerPw -IncludeTables 'T3_00,T4_01,T5_08,T5_09,T5_27' -Execute
& .\tools\land\Invoke-LandRecalc.ps1 -DocMap $map -PeriodKey 202601,202602,202603,202604,202605,202606,202607 -BaseUrl http://localhost:5094 `
    -ConfirmStand localhost:5094/EcrLandMsLegacy -SqlConnection $sql -User land-owner -Password $ownerPw -Execute
# 5. Звірка формульного шляху зі старою системою
& .\tools\land\Export-LandParityOracleFull.ps1 -Database EcrLegacyProd -Year 2026 -OutFile $sp\oracle.tsv
& .\tools\land\Compare-LandParityFull.ps1 -JsonDir $sp\sample -OracleTsv $sp\oracle.tsv -SqlConnection $sql -OutPrefix $sp\cmp-formulas -AggregateBy NaePhase
# 6. Перенесення
$m = 'Server=localhost;Database=EcrLandMsLegacy;Trusted_Connection=True;TrustServerCertificate=True'
$t = 'tools\Ecr.MethodologyScriptMigration\bin\Debug\net10.0\Ecr.MethodologyScriptMigration.dll'
dotnet $t convert --connection $m --out $sp\out
dotnet $t shadow  --connection $m --in $sp\out --i-know-this-is-a-copy
dotnet $t parity  --connection $m --out $sp\parity-all.json
# 7. POST /api/v1/methodologies/script-migration/switch, тіло {versionIds:[2,3,4,5,7,8,10,12], engine:"Script", reason:"..."},
#    перерахунок як у п. 4, звірка скриптового шляху:
& .\tools\land\Compare-LandParityFull.ps1 -JsonDir $sp\sample -OracleTsv $sp\oracle.tsv -SqlConnection $sql -OutPrefix $sp\cmp-script -AggregateBy NaePhase
```

Номери версій — ідентифікатори в `EcrLandMsLegacy` після кроку 3 (див. `convert-report.json`).

## 7. Що лишилося для справжнього етапу 8 на проді

1. **Правила й прив'язки 40 методологій.** З `AF_EmissionCalculationWork_Rules` (348 рядків) потрібні мапінг `RuleArg_Parameters/Values` → `MethodologyRule.MatchJson` (B13 §7, крок 5) і `RuleArg_TableLandFromDB` → прив'язки; цього шляху немає. Без нього C08, C09, C11, C12, `Flert`, `Thermaloxidizer`, C07_07_01, C07_16 не конвертуються. Додаткові етапи `Build-LandTemplate.ps1` для C11/C12 (`LandParity`, `CompositionMass`, `SubstanceApplicability`, `BranchGate` тощо) не запускались.
2. **Одиниці констант** `ECW_C05_05` (і, ймовірно, інших): без них `switch` відмовляє `script.unitMismatch`.
3. **`Common` і бібліотеки**: опублікувати й прогнати `importLibraryVersionsDiffer` / `switchGroupIncomplete` на справжній бібліотеці.
4. **HSE400 (рівень `Module`)** на реальних даних не проганявся: реальних входів 9 рядків `AF_AirGasCompositions_HSE400`; потрібен мапінг колонок `FG_*`/`LPG_*` у документ HSE400 і порівняння з `CalcResult_AirGasCompositions_HSE400` (9 рядків). Формул у `HSE400` дзеркала 341 (+ бібліотека), у `hse400-model.json` 369: відмінність не досліджувалась.
5. **Ємність таблиць у експортері**: брати з `MaxDynamicRows` кожної таблиці (T5_08).
6. **Вода, відходи, факели**: входи `AF_Water_*`, `AF_Waste_*`, `AF_Flarings*`, `AF_Air_HSE401*` не переносились (немає завантажувача).
7. Решта кроків розд. 8.6 (`pack`, `apply?dryRun`, вибірковий `parity` на живих даних, перерахунок «до/після») — у вікні за словом людини.

## HSE400 на реальних даних (19.11.2026)

Гілка `lane/ms-r-hsereal`. Джерело — та сама копія `EcrLegacyProd` (лише читання, `ApplicationIntent=ReadOnly`). Нова система для репетиції: окрема база `EcrHseMsLegacy`, API на порту 5095. Значень, імен і облікових записів прода тут немає: лише кількості й коди.

### Як стара система рахує HSE400 і звідки бере входи

- Вхід: одна таблиця `AF_AirGasCompositions_HSE400`, один рядок = один місяць (`EFID`, `Time`, 175 колонок вводу виду `<таблиця>_<компонент>` для шести таблиць `FG_Makat`, `FG_HMB`, `LPG_HMB`, `FG_Export`, `FG_Internal`, `LPG_Internal`, плюс два поля суми `*_HMB_TotalSumGasComposition`). У копії 9 рядків (2026-01…09), `_History` порожня. Місяці 01–07 мають ненульовий склад (на таблицю 13–35 ненульових полів), місяці 08–09 — усі нулі.
- Результат: `CalcResult_AirGasCompositions_HSE400`, 9 рядків × 490 значень (175 з них — луна вводу, 315 — результати формул).
- Формули: `AF_Formulas` методологія `HSE400` (341 рядок, 331 унікальне ім'я), спільна бібліотека `Common` (49 формул); константи `AF_Constants` HSE400 (23) і `Common` (281). Правил рядків немає (методологія зашита в `DllProject_Air_GasCompositions_HSE400`, B12). Формули мають версії за датою: 10 формул мають `V2` з 2025-01-01 (`GHGEmissionFactor` усіх шести таблиць, `TotalSulphurContent`/`MercaptanSulphurContent` у `*_HMB`); для 2026 діє `V2`.

### 341 проти 369 формул — не розбіжність моделі

| | Дзеркало прода | `hse400-model.json` |
|---|---|---|
| Формули HSE400 | 341 = 325 `HSE400_<таблиця>_<X>` + 6 `Common_LHV0_*` + **10 дублів `V2`** (одне ім'я — два тексти) | 331 імен (V2 замінює V1, версія методології `1.0.1.0` чинна з 2025-01-01) |
| Спільні формули `Common` | 49, окремою бібліотекою | 38, вбудовані в модель (ті, на які HSE400 посилається через `!`) |
| Разом | 341 + 49 | 331 + 38 = 369 |

Звірка за даними прода (текст формул без пробілів, чинна версія на 2026): **з 369 формул моделі розбіжних 0**; усі 331 імен HSE400 є в моделі й навпаки; 37 з 38 вбудованих спільних формул досяжні з HSE400, одна (`Common_WtCi_C2H6S`) недосяжна (зайва, на числа не впливає); 11 формул `Common_WtCi_*` дзеркала в моделі немає — на них HSE400 не посилається. Константи: усі 224 константи моделі посилаються з досяжних формул і мають **ті самі значення**, що в дзеркалі (розбіжних 0); решти констант `Common` (інші методології) модель не тягне. Дефектів моделі не знайдено. Обмеження, а не дефект: модель не має формул `V1` (2024) — десять формул `V1` лишилися поза моделлю, а у двох з них (`*_Internal_GHGEmissionFactor`, `V1`) є посилання на формулу `Common_WtCi_DimethilSulfide`, якої немає ні в `HSE400`, ні в `Common`; у копії даних за 2024 немає (`_History` порожня, рядки лише 2026), тож як стара система поводилася б на 2024, перевірити нічим.

Чому методологію взято з моделі, а не з імпорту дзеркала: `tools/Ecr.MethodologyImport --sql` дає пакет із 341 формулою рівня `Configuration` (без модуля місячних колонок), а `MonthColumnArgumentsModule` вимагає рівня `Module`; штатний будівник `Build-Hse400Template.ps1` створює саме його. Пакет із дзеркала побудовано й перевірено (HSE400: 341 формула, 23 константи, блокерів 0), але в `EcrHseMsLegacy` не імпортувався: код `HSE400` зайнятий Module-версією.

### Мапінг колонок старої таблиці → колонки шаблону HSE400

Код: `tools/hse400/Hse400Legacy.Lib.ps1` (тести `tools/tests/Hse400-Legacy.Tests.ps1`).

- Ввід: колонка `<код таблиці>_<ключ рядка>` → рядок вводу шаблону (таблиця `<код таблиці>`, колонка місяця `M01`…`M12` за місяцем `Time`). Усі 175 рядків вводу мають однойменну колонку; колонки без рядка вводу: `EFID`, `ModifiedTime`, `Time`, два `*_HMB_TotalSumGasComposition` (у шаблоні це розрахунковий рядок).
- Результат: колонка `<таблиця>_<X>` ↔ рядок `F_<X>` (315 з 325). Винятки: формула названа як рядок вводу (`Nitrogen` у `FG_Makat`/`FG_Export`, вісім імен у `*_Internal`) — результат у `<X>V2` (`NitrogenV2`) або `<X>_V2`; `Density20/0`, `FuelLowerHeating20/0` у `FG_Makat` — `Density_20` тощо; `MercaptanSulphurContent` у `*_HMB` зберігається як `TotalSulphurContentWithoutH2S`. 10 рядків `F_GHG1/F_GHG2` (проміжні кроки коефіцієнта викидів) у старій системі не зберігаються. Невикористаних колонок `CalcResult`, окрім луни вводу, немає (інакше звірка зупиняється з кодом 2).

### Результат

| Крок | Підсумок |
|---|---|
| Будівник (`Build-Hse400Template.ps1 -Stage All`) | шаблон 1.0.2.0, методологія 1.0.1.0 (Module, 369 формул, 224 константи) опублікована, 6 прив'язок, проєкти `HSE400_GOLD` і `HSE400_2026`; код виходу 0 |
| Завантаження вводу | 1575 комірок у 6 таблиць (9 місяців; нульові місяці теж) |
| `convert` | перетворено 1, відмов 0, зауважень 0 |
| `shadow` + `parity` (формули ↔ скрипт) на документі з реальними даними | 6 екземплярів таблиць, 500 рядків, **2925 значень (325 виходів × 9 місяців), розбіжностей 0** (на золотому документі ще 38, 0) |
| Контроль чутливості (у тіньовому тексті `k1_HSE400_` 0.8313 → 0.8314) | **1095 розбіжностей з 2925**; після повернення — 0 |
| Звірка зі старою системою, формульний шлях | порівняно **2835 комірок** (315 рядків × 9 місяців), **збіг 2835, розбіжностей 0**; точна рівність 2022, решта 813 — нулі й значення з відносною різницею не більше 9,3e-14; ненульових 2040 |
| `switch` на Script, перерахунок, звірка | той самий результат: 2835 / 2835 / 0, точна рівність 2022, максимум 9,3e-14 (усі лічильники звірки формульного й скриптового шляхів збігаються) |
| Контроль звірки (у вибірку внесено 2e-9 і 5e-10 відносної різниці) | 1 розбіжність (2e-9), друга в допуску: звірка не «сліпа» |

Групи розбіжностей зі старою системою: **немає** — класифікувати нічого. Властивість даних, яку звірка підтвердила: у нульових місяцях 08–09 стара система дає ненульові результати для `FG_Makat`, `FG_Export`, `FG_Internal`, `LPG_Internal` (залишковий азот 100 %: `NitrogenV2`, `MolecularWeight`, густини, об'єми — по 11 значень на таблицю), а для `*_HMB` — нулі (правило «нульова молекулярна вага → нулі»); нова система збігається в обох випадках.

### Знайдений дефект

`POST /api/v1/methodologies/script-migration/switch` відмовляв для версії рівня `Module` (HSE400) двома проблемами: `script.missingRows` (аналізатор скрипта в шляху авторства/`switch`/`apply` не передавав ознаку `IsModuleLevel`, хоча паритет і рантайм її передають) і `switch.problem.outputsDiffer` (скрипт `Module` оголошує вихід для кожної формули — 369, а збережена версія тримає лише виходи, прив'язані до якорів, і золотих тестів — 7). Виправлено: `ScriptAnalyzer` → `ScriptSourceCompiler.CompileAsync(isModuleLevel)`; для `Module` у `DiffAgainstStoredAsync` розбіжністю лишається лише збережений вихід, якого скрипт не дає. Тести: `ScriptMigrationHandlerTests` (`Switch_рівня_Module_приймає_скрипт_без_rows_…`, `…_збережений_вихід_якого_скрипт_не_дає_лишається_розбіжністю`, контроль `Switch_не_Module_без_rows_як_і_раніше_відмовляє_missingRows`). Корпус синтетики (3938 значень) цього не виявив, бо `switch` для `Module` там не запускався.

### Що не покрито

- Один об'єкт прода, один рік (9 місяців, з них 7 ненульових); 2024 (формули `V1`) у даних немає.
- Публікація скрипта `Module` шляхом авторства (`ScriptPublishChecks`) досі компілює без ознаки рівня й дала б `missingRows`; на цьому шляху не перевірялось (перемикання `switch`/`apply` працюють).
- Флейри, `HSE401*`, `Thermaloxidizer` не переносились.
- Луну вводу в `CalcResult` (175 колонок) зі завантаженим вводом не звірено окремо.

### Відтворення

Передумови як у розд. 6; нижче лише відмінні кроки. Паролі — у файлі поза репозиторієм (рядок `користувач<TAB>пароль`), створеному за зразком `rehearsal/01-api-import.ps1` (bootstrap → роль з усіма правами → користувач).

```powershell
$sp = 'C:\...\hse'     # тека поза репозиторієм
powershell -File tools\setup-dev-db.ps1 -Server localhost -Database EcrHseMsLegacy -Documents 0 -SmallFiles -DataPath F:\ecr-wt\upgdata-ms
# API на порту 5095 (ECR_ConnectionStrings__Ecr -> EcrHseMsLegacy), далі:
powershell -File tools\hse400\Build-Hse400Template.ps1 -Stage All -Year 2026 -BaseUrl http://localhost:5095 -Database EcrHseMsLegacy -ConfirmStand EcrHseMsLegacy -CredentialFile $sp\cred.tsv
powershell -File tools\hse400\Export-Hse400LegacySample.ps1 -OutFile $sp\sample.json            # EcrLegacyProd, лише читання
powershell -File tools\hse400\Compare-Hse400Legacy.ps1 -SampleFile $sp\sample.json -OutDir $sp\cmp -BaseUrl http://localhost:5095 `
    -Database EcrHseMsLegacy -ConfirmStand EcrHseMsLegacy -CredentialFile $sp\cred.tsv -Label formulas -Fill -Recalc
$m = 'Server=localhost;Database=EcrHseMsLegacy;Trusted_Connection=True;TrustServerCertificate=True'
$t = 'tools\Ecr.MethodologyScriptMigration\bin\Debug\net10.0\Ecr.MethodologyScriptMigration.dll'
dotnet $t convert --connection $m --out $sp\out
dotnet $t shadow  --connection $m --in $sp\out --i-know-this-is-a-copy
dotnet $t parity  --connection $m --out $sp\parity.json --document 2
# POST /api/v1/methodologies/script-migration/switch, тіло {versionIds:[1], engine:"Script", reason:"..."}, потім:
powershell -File tools\hse400\Compare-Hse400Legacy.ps1 -SampleFile $sp\sample.json -OutDir $sp\cmp -BaseUrl http://localhost:5095 `
    -Database EcrHseMsLegacy -ConfirmStand EcrHseMsLegacy -CredentialFile $sp\cred.tsv -Label script -Recalc
```

Номери версії (1) і документа (2) — ідентифікатори в `EcrHseMsLegacy` на чистій базі. Вибірка й звіти розбіжностей лежать лише в `-OutFile`/`-OutDir` поза репозиторієм (скрипти відмовляють, якщо шлях усередині git-worktree).

# tools/land: завантаження вкладки «2. Contract» шаблону Land

Скрипти PowerShell (Windows PowerShell 5.1 і PowerShell 7) працюють через REST API ECR.
Нічого не видаляють. Не запускати проти робочої бази замовника без рішення людини.

## На якому шаблоні запускати

`Apply-ContractHeader.ps1 -TemplateCode Land` потребує шаблон **Land**. Шаблон з фіксованими
таблицями без рядків (наприклад, DataGen) публікацію не пройде: `ECR-TMPL-4228`. Скрипт тоді
зупиняється з поясненням і переліком доступних шаблонів (`GET /api/v1/templates`).
Шаблон Land створюється імпортом методології: `tools/Ecr.MethodologyImport`.

## Порядок

1. Довідники LAND_*: `Import-ContractDictionaries.ps1` (спершу `-DryRun`).
2. Шапка: `Apply-ContractHeader.ps1 -TemplateCode Land` (спершу `-DryRun`).
   `-MigrateDocumentId 3,4` переносить документи на нову версію (Safe) і тоді, коли шапка вже
   на потрібній версії (0 змін). Dry-run міграції виконується завжди, застосування лише при `canApply`.
   Повторний запуск ідемпотентний: документ, що вже на цільовій версії, дає «документ N: вже на версії X — 0 змін»
   (код 0); інші відмови лишаються помилкою.
   ⚠ Список id: з сесії PowerShell — `-MigrateDocumentId 3,10`; через `powershell -File` кома дає один
   рядок, тому пишіть у лапках `-MigrateDocumentId "3,10"` (приймаються також `"3;10"` і `"3 10"`).
   Без лапок старіші версії скрипта читали «3,10» як 310.
   Документи переносяться лише на останню опубліковану версію (старіший `-SourceVersionId` без змін —
   помилка). Фонова міграція: відповідь 202 обробляється автоматично (опитування `GET /jobs/{id}`,
   `-MigrateTimeoutMinutes`, за замовчуванням 60). `-Async` просить сервер працювати у фоні.
   Якщо в шаблону вже є чернетка, скрипт зупиняється: вона може містити чужі незавершені правки.
   Перегляньте її, опублікуйте чи видаліть вручну або запустіть із `-UseExistingDraft`
   (несумісний з `-SourceVersionId`).

## Безпека стенда (RC21)

- Лише локальний стенд: `-BaseUrl` має вести на `localhost`, `127.0.0.1` або `[::1]`, а SQL (`-SqlConnection` у
  Build-LandTemplate) — на локальний сервер (`localhost`, `.`, `(local)`, `127.0.0.1`, `::1`, ім'я цієї машини, `(localdb)`)
  і на базу з іменем `EcrLand*` (за замовчуванням `EcrLandUi`): локальний прод-інстанс старої системи не проходить.
  Рядок підключення розбирається `SqlConnectionStringBuilder` (при дублі ключів перемагає останнє, як у SqlClient).
  Чужа адреса (в т.ч. https) - відмова ще до входу.
- Мутації — лише з явним `-ConfirmStand`. Apply-ContractHeader/Import-ContractDictionaries: адреса API (`localhost:5094`,
  `http://localhost:5094` або `localhost`; має збігатися з `-BaseUrl`). Build-LandTemplate: адреса І база —
  `localhost:5094/EcrLandUi` (або `http://localhost:5094/EcrLandUi`): URL API базу не називає, тож без імені бази
  підтвердження відхиляється. Без нього скрипти не роблять жодного не-GET виклику і SQL-запису; `-DryRun` лише читає.
  Build-LandTemplate передає дочірнім скриптам лише адресну частину сам.

## Адреса і вхід

- `-BaseUrl` або `$env:ECR_BASE_URL` (за замовчуванням `http://localhost:5092`).
- Обліковий запис: `-User` (або `$env:ECR_USER`) і `-Password` (SecureString). Пароль вводьте
  приховано: `$pw = Read-Host 'Пароль ECR' -AsSecureString`, далі `-Password $pw`. Пароль у відкритому
  тексті в командному рядку (`-Password 'пароль'`, `$env:ECR_PASSWORD = 'пароль'`) осідає в історії
  PSReadLine (у PowerShell 5.1 вона зберігається у файлі), тож так не робіть. `$env:ECR_PASSWORD`
  скрипти читають лише для автоматизації (CI, де значення дає менеджер секретів): змінна середовища
  видна всім дочірнім процесам сеансу, і скрипти не обіцяють її приховувати.
- **https**: працює як є.
- **http**: дозволено лише для localhost, 127.0.0.1, ::1. Сесійний cookie має прапор Secure і по
  http його не надсилає Windows PowerShell 5.1 (інакше 401), тому скрипт копіює cookie без Secure
  тільки для loopback. Для інших адрес по http скрипт відмовляє до відправлення пароля:
  використовуйте https.

## -DryRun

`-DryRun` (або `-WhatIf`) виконує лише читання: показує, що було б змінено.

```powershell
$pw = Read-Host 'Пароль ECR' -AsSecureString   # прихований ввід, до історії не потрапляє
.\Import-ContractDictionaries.ps1 -BaseUrl http://localhost:5092 -User admin -Password $pw -DryRun
.\Import-ContractDictionaries.ps1 -BaseUrl http://localhost:5092 -User admin -Password $pw
.\Apply-ContractHeader.ps1 -TemplateCode Land -BaseUrl http://localhost:5092 -User admin -Password $pw -DryRun
.\Apply-ContractHeader.ps1 -TemplateCode Land -BaseUrl http://localhost:5092 -User admin -Password $pw -MigrateDocumentId 3,4
# з cmd / планувальника (powershell -File): список у лапках
powershell -NoProfile -File .\Apply-ContractHeader.ps1 -TemplateCode Land -MigrateDocumentId "3,10"
```

## Повна побудова шаблону Land і бази UI-приймальної

`Build-LandTemplate.ps1` будує з `docs/delivery/reference-data/land-template/land-template.model.json` весь шаблон Excel
`RDS_xxx_Template_Land_2026_v1.0.4.0` (20 звітних аркушів, 83 таблиці, ~5 тис. колонок), довідники (`LAND_*`, у т.ч. «2. Contract»),
шапку Contract (13 полів, через `Import-ContractDictionaries.ps1` і `Apply-ContractHeader.ps1`), методології AF (рули, прив'язки,
golden-тести, публікація другою особою), проєкт, ролі й користувачів, документи. Ідемпотентний: повтор = 0 змін.

```powershell
.\Build-LandTemplate.ps1 -BaseUrl http://localhost:5094 -BootstrapPassword (Read-Host 'bootstrap' -AsSecureString) -Stage All
.\Verify-LandTemplate.ps1 -BaseUrl http://localhost:5094 -User land-owner -Password (Read-Host 'land-owner' -AsSecureString)
```

Формули рівня комірки (D1): у моделі таблиці 7.0 лежить `cellFormulas` (216 перехресних формул 7.0 ← 7. Water Report виду
`(a+b+c)/1000`); `Put-CellFormulas` записує їх `PUT /api/v1/template-versions/{id}/tables/{tableDefId}/formulas/cell/{rowKey}/{columnDefId}`
після побудови структури (потрібен сервер із цим маршрутом; колонка комірки в моделі має тип `Formula`). `-DryRun` друкує їх кількість.

Модель Excel -> JSON перегенеровується скриптом `model-gen\run-model-gen.ps1` (Python 3.12 + openpyxl). Деталі й правила
відображення — `docs/delivery/reference-data/land-template/README.md`.

## Паритет розрахунків: стара система <-> ECR (локально, прод лише для читання)

Інструмент порівнює результати, ЗБЕРЕЖЕНІ старою системою (CalcResult_Emissions_*), з розрахунком ECR на тих самих
вхідних (документи LAND-PROD-n стенда). Значення прода НІКОЛИ не потрапляють у репозиторій, хмару чи листи:
файли еталона й результату пишуться лише ПОЗА репозиторієм (скрипти відмовляють шляху всередині репо), у звітах
лише ідентифікатори, кількості, класи розбіжностей і відносні похибки.

1. Export-LandParityOracle.ps1 -OutFile <поза репо> [-Months 1,2,5,7] - лише SELECT ... WITH (NOLOCK) під READ
   UNCOMMITTED у локальній прод-БД (-Database ECR); повторює вибірку експортера зразка (один EFID на таблицю й
   місяць) і зберігає по рядку: підпис вхідних (для вирівнювання) і збережені результати за (EFID, місяць, NAE).
2. Перерахувати документи стенда (завантажувач або POST /api/v1/documents/{id}/recalculate).
3. Compare-LandParity.ps1 -JsonDir <каталог зразка> -OracleFile <...> -OutFile <поза репо> [-TolAbs 1e-9 -TolRel 1e-12]
   читає calc.CalculationResult поточного прогону з локальної EcrLand* (SQL, без API й без записів) і групує
   розбіжності за методологією / виходом / класом. Без -IncludeValues значення у вихідний файл не пишуться.
   -EcrResultsFile - офлайн-режим (тести, архівні прогони).

Допуск: сховище calc.CalculationResult.Value має 12 знаків після коми, тому |d| <= 5e-13 - це лише округлення
(MATCH; MATCH_ROUNDING_LOSS, якщо відносна похибка при цьому > 1e-9); |d| <= -TolAbs (1e-9) - NEAR (відносна
похибка в таблиці); решта - MISMATCH_* (напрямок: ECR_ZERO, LEGACY_ZERO, відношення x1000 / 3.6 / 3600 ... ).
Причини відсутності результату розділено: MISSING_NOT_IN_ECR, MISSING_NOT_PUBLISHED (методологію не опубліковано),
MISSING_NO_BINDING (немає прив'язки до колонки результату), MISSING_BOUND_NO_RESULT (рядки відхилено правилом
категорії / не завантажено lookup), MISSING_ROW, MISSING_OUTPUT. Стара система не пише Land_No у результат:
рядки зі спільним NAE (і тим самим паливом) позначаються AMBIGUOUS_KEY і не порівнюються.
Сторожі: LandParityHarnessTests (Architecture).

### Повний паритет (усі документи, усі періоди, кілька EFID)

Для повного набору (документи `LAND-PROD-n.json` із кількома періодами, кілька EFID прода) — окремі скрипти, наявні
сигнатури вище не змінено. Усе локально, поза репозиторієм; у виході (summary / cells) значень немає.

1. `Export-LandParityOracleFull.ps1 -OutFile <поза репо>.tsv [-Year 2026] [-Months 3]` — усі збережені результати
   `CalcResult_Emissions_Air_Land*` року в TSV (efid, місяць, таблиця, NAE, компонент, фаза, tons, gsec, методологія,
   час, джерело). Лише `SELECT ... WITH (NOLOCK)`, локальний сервер, БД `ECR`. **Файл зі значеннями - не комітити.**
2. Перерахувати документи стенда (Current-прогони за всі періоди).
3. `Compare-LandParityFull.ps1 -JsonDir <каталог> -OracleTsv <...> -SqlConnection <EcrLand*> -OutPrefix <поза репо>`
   → `<OutPrefix>.summary.json` (класи загалом `classes`, по методологіях `byMethodology`, групи
   методологія/таблиця/клас з медіаною, p95, max відносної похибки) і `<OutPrefix>.cells.tsv` (не-MATCH клітинки:
   документ, період, таблиця, rowKey, кількість рядків, EFID, методологія, вихід, клас, відносна похибка).
   Офлайн (тести) замість SQL: `-StateFile` (JSON `methodologies` / `tables` / `results`).

Що відрізняє від harness зразка: (1) рядки вирівнюються **за позицією** `p<doc>-<seq>` (той самий JSON — вхід
завантажувача), а не за підписом входів; (2) мапа методологія → таблиця береться з `cfg.CalculationBinding`,
`-MethodologyTable '<regex>=<Т>[+<Т>]'` доповнює методології без прив'язки (C08 / C11 / C12), далі вбудована мапа
C05 / C06_01 / C07_*; для методології з кількома таблицями результат приписується тим, де рядок існує, а
невирішені випадки лічить `multiTableResults` у summary (ключ рядка результату таблиці не містить); (3) нові
класи `ROW_NOT_LOADED_CAPPED` (рядок прода є, у документі ECR його немає, а таблиця вже має `MaxDynamicRows`
рядків - ліміт шаблону) і `ROW_NOT_LOADED_NO_CELLS`; суфікс `_ZERO` - результат старої 0; допуски й решта
класів ті самі (MATCH ≤ 5e-13 abs; MATCH_ROUNDING_LOSS при rel > 1e-9; NEAR ≤ 1e-9).

Параметри: `-Months 3` (лише ці місяці; дефолт - усі), `-Documents LAND-PROD-5,...` / `-MinDocument` / `-MaxDocs`,
`-GroupBy '<Т>=<Кол>[+<Кол>]'` (ключ групи NAE замість авто-колонки фази; для C12_01; перша колонка - ще й фільтр
`Component_Phase` старої), `-AggregateBy NaePhase`, `-ValidateOnly` (лише розібрати й надрукувати параметри).

**`-AggregateBy NaePhase`** (для AMBIGUOUS_KEY): результат старої не має Land_No, тож клітинку старої за
(EFID, NAE, фаза) порівнюють із **сумою** значень ECR за рядками групи; одна клітинка на (група, методологія,
вихід). Класи з префіксом `AGG_`: `AGG_MATCH`, `AGG_MATCH_ROUNDING_LOSS`, `AGG_NEAR`, `AGG_MISMATCH*`,
`AGG_MISSING_*`, `AGG_ROW_NOT_LOADED_*`; сума, що не збіглась при частково не завантажених рядках групи, -
`INCOMPLETE_ROW_NOT_LOADED_*` (не дефект виразу). Допуск зберігання множиться на кількість доданків. Без
`-AggregateBy` ключі лишаються `AMBIGUOUS_KEY` (порахований так само, як у harness зразка).

Сторожі: LandParityFullHarnessTests (Architecture): експортер лише читає, вихід не в репо, розбір параметрів,
класи на синтетичній фікстурі без даних прода, у файлах виходу значень немає.

## Довідник повітряних речовин (LP-07, форма B константи)

`Import-LandAirSubstances.ps1` завантажує LAND_AIR_SUBSTANCE з **локальної копії** прода (`AF_PermitConstant`,
лише `SELECT … WITH (NOLOCK)`; значень у git немає) у стенд через REST: записи `E<код>` з полем SUBSTANCE_CODE
(3-4 цифри, без провідного нуля); повтор - 0 змін. Параметри: `-SourceDatabase`/`-ConfirmSource` (локальна копія),
`-ConfirmStand`; `-AttachMethodologyId/-AttachVersionId/-AttachCodes` додає речовини до складу версії-чернетки
(`PUT /api/v1/methodologies/{id}/versions/{vid}/substances`). Спершу `-DryRun`.

Етап `Build-LandTemplate.ps1 -Stage LandParity` (лише явно, в `All` не входить) для імпортованих C11/C12-чернеток:
задає правило категорії з `af-land-category-rules.json`, склад речовин версії з суфіксів `_(\d{3,4})$` категорій її
констант (записи `E<код>` довідника LAND_AIR_SUBSTANCE) і додає в тест-кейс `PARITY_1` аргументи, які читає правило
(`@Land_NAE`, `@Land_TypeFuelOrStream`, `@Land_Streams`, `@Land_InvNumEqp`, `@Land_TypeStoredMaterial`); наявні
аргументи, очікуване й допуск не змінюються. Чиста логіка — `LandParityRuleArgs.Lib.ps1` (тест у Architecture).

### Порядок етапів: LandParity ДО Methodologies

`-Stage Methodologies` створює прив'язки колонок результатів C08/C09/C11/C12 (LP-08; до чернеток - вимкнені), а серверна
перевірка правила категорії (R16-4) звіряє `@`-аргументи з колонками таблиць УСІХ прив'язок методології, вимкнених
теж. Тому після `Methodologies` кожен `PUT .../category-rule` дає `422 ECR-CALC-0422 categoryRuleUnknownArgument`
(колонки названі `TypeOfFuel`, `IDCodeNAEProject`, а правило читає `@Land_*`). Порядок для стенда:

1. імпорт C11/C12 (`tools/Ecr.MethodologyImport`);
2. `-Stage LandParity` - правила, склад речовин, аргументи PARITY_1;
3. `-Stage Methodologies` (повторно) - створює прив'язки.

У одному запуску (`-Stage LandParity,Methodologies`) скрипт сам виконує LandParity першим. Етап друкує підказку,
якщо побачив `categoryRuleUnknownArgument`: прив'язки вже створено, API їх не видаляє - беріть стенд, де LandParity
виконано до повторного Methodologies. Симуляція PARITY_1 шле `periodKey` числом (`{year}06`), як `SimulateMethodologyRequest`.
Попередження для наступних етапів: правило з `@Land_*`, не знайденими в колонках прив'язаної таблиці, так само не
пройде публікацію (`PublishMethodologyHandler`, той самий код) - узгодження імен аргументів і колонок потрібне до
активації C11/C12.

### Після publish прив'язки вимкнені, поки повторно не запущено `-Stage Methodologies`

Прив'язки колонок результатів методологій C08/C09/C11/C12 лишаються ВИМКНЕНИМИ після публікації версії методології
(`publish`), доки не запущено `-Stage Methodologies` ще раз. Порядок для стенда з уже імпортованими C11/C12:

1. `publish` версій методологій (імпорт C11/C12 лишає чернетки);
2. `-Stage Methodologies` (повторно) - активує прив'язки опублікованих версій;
3. `-Stage CompositionMass` - за потреби (мапа складу газу, LP-10).

Без кроку 2 колонки результатів порожні, хоча версії опубліковано (перевірено на клоні 1.0.9.0, звіт верифікації 13.10).

### Стелі динамічних рядків наявних таблиць клону (D-UPG-1)

`-StructureMode Sql` на клоні підтягує `MaxDynamicRows` НАЯВНИХ таблиць чернетки до `maxRows` моделі, але лише
розширенням (стеля нижча за модель піднімається; рівна, вища чи відсутня - не чіпається). Дія ідемпотентна, у лозі:
`таблиць MaxDynamicRows піднято: N` (повторний запуск - 0). `Verify-LandTemplate.ps1` порівнює стелі структури версії з
моделлю й видає рядки `SHEET/TABLE: стеля динамічних рядків було -> має бути` (код виходу 1). Без цього клон лишав
стелі зі старої версії, і завантаження відхиляло рядки `ECR-ROW-0409`. Чиста логіка - `LandTemplateDiff.Lib.ps1`
(`Get-LandMaxRowsPlan`, `Get-LandMaxRowsMismatches`; тест `LandMaxRowsUpdateTests`).

## Склад газу HSE400 для методологій C11/C12 (LP-10)

Стара система бере склад із `dbo.CalcResult_AirGasCompositions_HSE400` (рядок = місяць, колонка = `{префікс потоку}_{показник}`),
а потік вибирає значення Land-поля типу палива/потоку через словник префіксів. У ECR це два довідники без міграції й
без нових функцій рушія: `LAND_HSE400_STREAM` (текст палива -> префікс) і `LAND_HSE400_COMPOSITION` (нормалізований:
`YEAR, MONTH, STREAM, INDICATOR` -> `VALUE`, складений первинний ключ). Аргумент методології (Density20, Vddg ...) - це
міст-колонка шаблону на `REGFIND`/`REGFIELD` (генерується `model-gen/hse400_bridges.py`).

`Import-LandHse400Composition.ps1` одноразово завантажує значення з **локальної копії** прода (лише `SELECT … WITH (NOLOCK)`;
значень у git немає, у вивід - лише кількості) у стенд через REST (`entries/batch`): показник = канонічний суфікс колонки
(`_20` -> `20`, `_0` -> `0`); HMB: `EMercaptan_mass` береться з `EMercaptanV2_mass`; запасна: порожній `C6_C10_mass` -> `C6_1_mass`.
`-DiscoverStreams` додає до словника потоків тексти Land-полів, що збігаються з правилом старої системи (підрядок, без
регістру, в обидва боки). Повтор - 0 змін; наявні записи не чіпаються. Параметри як у `Import-LandAirSubstances.ps1`
(`-SourceDatabase`/`-ConfirmSource`, `-ConfirmStand`); спершу `-DryRun`.

Немає запису за (рік, місяць, потік, показник) -> `REGFIND` дає `#N/A` -> аргумент не обчислено -> рядок без результатів
(стара система: `continue`, мовчки). Це одне правило, а не помилка даних: склад є лише за місяці, які мала стара система.
Не мостяться (див. `argumentBridges.unresolved` моделі): `CompositionMass` - не міст, а підстановка у формулу (нижче); таблиці без атрибута
типу палива/потоку в AF-класі (9a_17a, 9a_19, 9a_21..23). Для 9a, 10_03 і 11_03 колонка-джерело потоку береться з
атрибута AF, який читає старий код (`af_attr_bridges.stream_sources`; мости атрибутів AF - розділ README шаблону
«Мости за атрибутами AF-таблиць»). Сторожі: LandHse400CompositionScriptTests, LandHse400BridgesModelTests (Architecture),
LandHse400CompositionRegistryTests (Infrastructure, реальний SQL).

### CompositionMass: склад по речовині

`{код}` у `{код}_mass` - числовий код забруднювальної речовини (`SUBSTANCE_CODE` довідника `LAND_AIR_SUBSTANCE`,
`AF_PermitConstant.Substance_Code`), а колонки прода названі ХІМІЧНО (`H2S_mass`, `C1_C5_mass`, `EMercaptan_mass`), тож
стара система зіставляє жорсткою таблицею `PollutionCodeService.GetMassName` (15 кодів; решта -> `Unknown_mass` -> колонки
немає). У ECR це довідник `LAND_HSE400_SUBSTANCE` (ключ `SUBSTANCE_CODE`, поле `INDICATOR`; значень прода немає - це
відповідність «код -> ім'я колонки», береться з моделі). ⚠ REGFIND по `LAND_AIR_SUBSTANCE` за номером речовини не працює:
код запису там `E333` (EcrCode вимагає літери), а SUBSTANCE_CODE = `333`, первинного ключа в описі немає.

У шаблоні колонки-моста бути не може (діалект без `CURRENT_SUBSTANCE()`), тому в формулі ЧЕРНЕТКИ версії методології
(`ECW_C11_01/02/10`, `ECW_C12_01/03`) токен `@CompositionMass` замінюється фрагментом з
`argumentBridges.hse400.compositionMass.methodologies.<ім'я>.fragment`: `Build-LandTemplate.ps1 -Stage CompositionMass`
(лише явно; опубліковані версії не чіпає; повтор - 0 змін). Фрагмент вживає `CURRENT_SUBSTANCE()` і `REGFIND`/`REGFIELD`
(ярус Extension): у версії `NumericMode = Legacy` публікація дає ECR-CALC-0433, тому етап **сам переводить чернетку з
фрагментом у `Strict`** (`PUT /methodologies/{id}/versions/{vid}/modes`, календарний режим і журнал не чіпає;
`-KeepNumericMode` вимикає). ⚠ `Strict` змінює арифметику версії: `decimal` замість `double` NCalc 1.3.8, ділення на нуль -
`#DIV/0` замість `+∞`, `Round` - AwayFromZero; у формулах C11_01/02/10, C12_01/03 дільники - лише константи `CST.k*`
(змінних знаменників немає), `Pow`/`Sqrt` - лише в C11_02 (константні аргументи), а `if(@X = 0, 0, …)` не залежить від
режиму. Аргумент потоку фрагмента (`@Land_TypeFuelOrStream` у C11_10) має бути колонкою на КОЖНІЙ таблиці методології:
де старий код читає `Land_TypeFuel` (T9_02, T9_07, T9_11, T9a_24..26, T9a_28), модель дає міст до тієї самої колонки
джерела потоку (`hse400_bridges.STREAM_ARGUMENT_ALIASES`). Довідник `LAND_HSE400_SUBSTANCE` на стендах, побудованих до цієї зміни, заводить
`Import-LandHse400Substance.ps1` (`-DryRun`, `-ConfirmStand`; прод не читає). Сторожі: LandHse400SubstanceMapTests
(Architecture), CompositionMassBySubstanceTests (Expressions), LandHse400SubstanceRegistryTests (Infrastructure, реальний SQL).


### Застосовність речовини на рядок (LAND_SUBSTANCE_APPLICABILITY)

Склад речовин версії один на ВЕРСІЮ, а старий C# перебирає список кодів ПО РЯДКУ: `pollutionCodeByFuelType[prefix]`
(C11_01/02/10, C12_03; префікс - `PrefixProvider` за текстом палива/потоку), `polCodeList[typeOfStream]` (C12_01),
`pollutionCodeByInvNum[InvNum з Т/А кирилицею -> латиниця]` (C12_04_01), `fuelShort` diesel|oil (C12_04_02) і списки
`NAE -> коди` (C11_11_03, C11_11_05, C11_19). «Чужа» речовина рядка там просто не рахується, а в ECR давала б зайвий рядок
результату (значення з фолбека `Common`) або `#REF`. Довідник `LAND_SUBSTANCE_APPLICABILITY` переносить цю таблицю в
дані БЕЗ міграції: ключ `(METHODOLOGY, SELECTOR_KIND, SELECTOR_VALUE, SUBSTANCE_CODE)`, регістр значущий;
`SELECTOR_KIND` = `NAE` | `InvNum` | `TypeFuel` | `Stream`; запис `(методологія, '*', '*', '*')` - маркер «методологія має
застосовність» (без нього функція не фільтрує).

- Дані: `land-substance-applicability.json` складає `model-gen/extract_substance_applicability.py` зі словників C#
  (`--source-root <каталог docs/source основного клону>`; `--check` порівнює з файлом, код 1 при розбіжності; прод не
  читається), довідник у моделі - `model-gen/substance_applicability.py` (домен текстів потоку: `LEGACY_TEXTS`, довідники
  шаблону типу палива/потоків і літерали `in(@arg, ...)` правил категорії; InvNum - усі Т/А-варіанти кирилиця/латиниця;
  NAE - канонічне написання й `NNNN (NNN)`). Опис - `substanceApplicability` моделі (`methodologies.<ім'я>.kind`,
  `valueExpression`).
- Рушій: `SUBSTANCE_APPLICABLE(registry, scope, kind, value)` (`docs/build/02b-expressions.md`, «Застосовність речовини до
  рядка»). Правило категорії, що її містить, у циклі речовин обчислюється для кожної речовини; порожній ключ = речовину для
  рядка пропущено (виходів і помилок немає, крок трейсу `SubstanceNotApplicable`).
- Стенд: `Build-LandTemplate.ps1 -Stage Registries` (довідник) і далі `-Stage SubstanceApplicability` (лише явно; обгортає
  правило категорії ЧЕРНЕТКИ: `if(SUBSTANCE_APPLICABLE('LAND_SUBSTANCE_APPLICABILITY','<методологія>','<вид>',<значення>),
  <правило>,'')`; опубліковану версію не чіпає; повтор - 0 змін; `-Stage LandParity` обгортку не скидає). Версія має бути
  вище за Legacy (ECR-CALC-0433). Сторожі: LandSubstanceApplicabilityTests (Architecture), SubstanceApplicableFunctionTests
  (Expressions), SubstanceApplicabilityModuleTests (Calculations), SubstanceApplicabilitySqlTests (Api, реальний SQL).
- ⚠ Модель перегенеровується ПІСЛЯ lane, що теж міняють `land-template.model.json` (`run-model-gen.ps1` на вершині).

## Похідні аргументи зі словників C#: групи NAE і Land_MassFraction (LP-10)

Залишок 0438 після мостів за атрибутами AF мав три аргументи, які старий код виводить зі словників у C#. Рушій не
змінено: вистачає REGFIND/REGFIELD/CURRENT_SUBSTANCE() і `if`.

- **`Land_Power`** (`ECW_C11_03`, лише таблиця 9a_17) і **`Category1`** (`ECW_C11_11_02`, 9_01/9_07/9_08/9a_16/9a_17a/9a_19) -
  довідник `LAND_NAE_GROUP` (ключ `NAE_NORMALIZED`, поля `POWER_GROUP` Decimal і `CATEGORY1`). Записи ГЕНЕРУЮТЬСЯ з C#
  (`model-gen/extract_nae_groups.py <docs/source> af-land-nae-groups.json`, далі `run-model-gen.ps1`); прод не читається.
  Міст: `REGFIELD(REGFIND('LAND_NAE_GROUP', [<колонка NAE>]), 'POWER_GROUP' | 'CATEGORY1')`; немає запису -> `#N/A` ->
  рядок без результату (стара: «Error Power value» / «Error category1 - NAE», `continue`). Нормалізація NAE: у `C11_11_02`
  пробіли знімаються, у `C11_03` - ні; діалект шаблонів не має заміни тексту, тому для Category1 є обидва написання
  (`0788(001)` і `0788 (001)`), а краї й послідовності пробілів знімає сам REGFIND. Довідник на наявних стендах заводить
  `Import-LandNaeGroup.ps1` (`-DryRun`, `-ConfirmStand`; повтор - 0 змін), на свіжих - `-Stage Registries`.
- **`Land_MassFraction`** (`ECW_C11_11_04`, 9a_19) - аргумент береться з атрибута `Land_MassFraction_0<код>` за поточною
  речовиною проходу. Речовин type1 п'ять (415, 416, 602, 616, 621), тому: п'ять мостів `Land_MassFraction_0<код>` на
  колонки із заголовком «<код>: ...» і фрагмент `if(CURRENT_SUBSTANCE() = '415', @Land_MassFraction_0415, ...)` замість
  `@Land_MassFraction` у формулі ЧЕРНЕТКИ: `Build-LandTemplate.ps1 -Stage MassFraction` (разом із `-Stage CompositionMass`;
  чернетці з Legacy ставиться Strict; опубліковане не чіпає; повтор - 0 змін).

Сторожі: LandNaeGroupBridgesModelTests (Architecture), NaeGroupBridgesTests (Expressions, справжній рушій).


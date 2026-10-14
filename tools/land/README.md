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
   `-MigrateTimeoutMinutes`, за замовчуванням 60). Застосування міграції (LP-43) за замовчуванням іде у
   фоні: у тілі `async: true`, сервер відповідає 202 + `jobId`, скрипт опитує задачу до завершення
   (великий проєкт не впирається в таймаут HTTP). `-Synchronous` — явний синхронний режим (200 зі
   звітом); `-Async` застарів і нічого не змінює. Сухий прогін завжди синхронний.
   Якщо в шаблону вже є чернетка, скрипт зупиняється: вона може містити чужі незавершені правки.
   Перегляньте її, опублікуйте чи видаліть вручну або запустіть із `-UseExistingDraft`
   (несумісний з `-SourceVersionId`).

## Безпека стенда (RC21)

- Лише локальний стенд: `-BaseUrl` має вести на `localhost`, `127.0.0.1` або `[::1]`, а SQL (`-SqlConnection` у
  Build-LandTemplate) — на локальний сервер (`localhost`, `.`, `(local)`, `127.0.0.1`, `::1`, ім'я цієї машини, `(localdb)`)
  і на базу з іменем `EcrLand*`: локальний прод-інстанс старої системи не проходить. ⚠ `-SqlConnection` **не має
  значення за замовчуванням** (раніше цілив у живий `EcrLandUi`): задайте його явно або дайте `-ConfirmStand
  <адреса>/<база>` — база `EcrLand*` береться з нього (`localhost:5101/EcrLandAccP` → `EcrLandAccP`, сервер
  `localhost`); без жодного з двох (навіть із `-DryRun`) скрипт відмовляє. Іменований екземпляр — лише явним `-SqlConnection`. Те саме для `Compare-LandParity.ps1` і
  `Compare-LandParityFull.ps1`: `-SqlConnection` без дефолту, обов'язковий у SQL-режимі (без `-EcrResultsFile` / `-StateFile`).
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

### C12_02 / C12_03: зіставлення імен виходів (репетиція вікна 13.10, B4)

Стара система зберігає результат ключем `(методологія, компонент = код)`, тобто `ECW_EC_tons_<код>`. ECR називає виходи
інакше, тож без нормалізації C12_02/C12_03 давали лише `MISSING_OUTPUT`/`ECR_ONLY`/`LEGACY_STATUS_SKIP`. Тепер
`Add-LandParityEcrResult` зводить імена (`ConvertTo-LandParityCanonicalOutput`):

| Вихід ECR | Умова | Ключ старої системи |
|---|---|---|
| `ECW_EC_tons\|gsec_XXX` | у результата є речовина (SUBSTANCE_CODE запису довідника, інакше код запису) | `ECW_EC_tons\|gsec_<код речовини>` (C12_03) |
| `ECW_EC_tons\|gsec_XXX` | речовини немає | лишається `XXX` -> `ECR_ONLY`; кількість - `outputsWithoutSubstance` у summary |
| `ECW_EC_tons\|gsec_<код>_type1\|_type2` | методологія зі списку TypeFold (за замовчуванням `^ECW_C12_02$`, збігається з етапом ParityBranch) | `ECW_EC_tons\|gsec_<код>` |
| обидві гілки одного ключа | TypeFold | клас `BRANCH_BOTH_PRESENT` (значення не порівнюється: старий код пише одну гілку) |
| `ECW_EC_*_415_type1` | NullType1 (C12_02: код 415) | лишається повним іменем -> `ECR_ONLY` (type1 для 415 у старому коді немає; ParityBranch ставить йому `NULL`) |
| `ECW_EC_*_<код>*` з речовиною проходу, відмінною від `<код>` | будь-яка | пропускається, лічильник `foreignSubstanceIgnored` |

D-425, «лише Running»: вихід ECR, якого немає в легасі, у групі з рядком, що має колонку статусу не `Running`
(або порожню), - клас `ECR_NON_RUNNING` (`AGG_ECR_NON_RUNNING`) замість `ECR_ONLY`; лічильник - `ecrNonRunningCells`
у summary, після фільтра (`-Stage RowFilter`, `land-row-filter.json`) має бути 0. Групи таблиць без колонки статусу
не позначаються.

Таблиці за замовчуванням (коли немає прив'язки): `^ECW_C12_02$` -> `T10_03+T11_03`, `^ECW_C12_03$` -> `T10_01+T11_01`.
Фаза/групування C12_*: колонка `Land_TypeFuel` знаходиться сама; для `Land_TypeStream` (T11_03) задайте
`-GroupBy 'T11_03=Land_TypeStream'`. Інші методології з гілками - `-TypeFoldMethodology '<regex>[;<regex>]'`.

Звіт (без значень): у `summary.json` - `buckets` (по методології) і `bySubstance` (методологія x речовина), у
`<OutPrefix>.buckets.tsv` - ті самі рядки: `matched`, `withinThreshold` (збіг лише завдяки порогу `-RelThreshold`,
за замовчуванням `1e-9`; NEAR/MISMATCH* з відносною похибкою ≤ порога), `mismatch`, `ecrOnly`, `oracleOnly`
(MISSING_*, ROW_NOT_LOADED_*, LEGACY_STATUS_SKIP, METHODOLOGY_DIFF), `other` (AMBIGUOUS_KEY, LEGACY_DUP_CONFLICT,
INCOMPLETE_*, BRANCH_BOTH_PRESENT), `zero` (комірки з нульовою старою), `relP95`, `relMax`. Сторож -
LandParityC12CompareTests (Architecture; синтетика, мутаційні докази: гілка не знімається, речовина не підставляється,
обидві гілки не виявляються).

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

### Порядок етапів стенда після оновлення шаблону (RC32/RC33): повний цикл

Порядок етапів одного прогону (кожен - лише явно або в складі `All`, як зазначено вище):

1. `Template` БЕЗ `-PruneStaleTables` - нова чернетка шаблону з моделі;
2. `Registries`, далі імпорти довідників (`Import-LandAirSubstances.ps1`, `Import-LandHse400Substance.ps1`,
   `Import-LandHse400Composition.ps1`, довідник NAE-груп);
3. `LandParity,Methodologies` (LandParity ДО Methodologies - див. вище);
4. `SubstanceApplicability`, `CompositionMass`, `MassFraction` (останній також входить у `CompositionMass`);
5. `ParityBranch` - лише C12_02 (див. нижче).

⚠ **`-PruneStaleTables` - не на першій чернетці після публікації.** Прибирання застарілих таблиць ДО перенесення версії
документів блокує перенос (`dataLoss`). Правильний порядок (знахідка F1 RC32):

1. `Template` БЕЗ `-PruneStaleTables`;
2. `publish` версії;
3. `migrate-version` документів з `tableRenames` (T9a_03/06/07 -> T9a_17a/21a/21b);
4. `-PruneStaleTables` - лише на НАСТУПНІЙ чернетці, коли документи вже на новій версії.

⚠ **Не логінтеся тим самим акаунтом паралельно під час `Verify-LandTemplate.ps1`** (знахідка F2 RC32): новий вхід
обриває сесію, яку тримає Verify, і запити після цього дають 401. `Verify` запускайте без повторів (повтор обриває сесію
ще раз) і не відкривайте під цим акаунтом UI/інші скрипти, доки він іде.

#### ParityBranch: вибір type1/type2 у C12_02

Старий код `ECW_C12_02` для кожного компонента (301/304/330/337) рахує ЛИШЕ `type1`, якщо `Land_Measure_Component_<код>`
має значення, інакше лише `type2`; код 415 - завжди `type2`. В ECR обидві гілки - виходи (`ECW_EC_tons|gsec_<код>_type1/_type2`),
тож за наявного значення вийшли б обидві. `-Stage ParityBranch` (лише явно, в `All` не входить, лише чернетки, не публікує)
обгортає формули `type2` компонентів 301/304/330/337:
`if(@Land_Measure_Component_<код> = NULL, <вираз type2>, NULL)` і додає аргумент до списку формули. `type1` і код 415
не змінюються; повтор - 0 змін. Усі елементи `Land_Measure_Component_*` у списку аргументів цих формул `type2` позначаються
маркером необов'язковості `?` (D-397; чужі елементи цілі; уже обгорнута чернетка без `?` лише отримує маркер): міст GTU
(MAX по порожній множині) не зберігає комірку, коли рядка-джерела з компонентом немає, і без `?` аргумент не збирається
(`#ARG`) - `type2` не рахувався б, хоча старий код його рахує. З `?` відсутня комірка = null, `null = NULL` - TRUE.
Маркер у рушії діє на всю версію (union): `type1`-формули того ж компонента за порожнього компонента дадуть null (`#NULL`
у трасі) замість `#ARG` - числа однаково немає. Запускати ПІСЛЯ `Methodologies` (створює формули) і ПЕРЕД `publish`. Чиста логіка -
`LandParityBranch.Lib.ps1` (тест `LandParityBranchTests`). Вихід без числа не пишеться, у трасі лишається `#NULL` для
неактивної гілки. Рушій, контракт і міграція не змінюються.

Код 415 (той самий етап): у старому коді `Land_Measure_Component_415` не передається, тож для 415 завжди `type2`, а
`ECW_EC_(tons|gsec)_415_type1` ніколи не пишеться. В AF-пакеті цей `type1` існує з виразом, ідентичним `type2`, тому в ECR
вийшли б обидва з однаковим числом. Етап ставить у `ECW_EC_(tons|gsec)_415_type1` вираз `NULL` (вихід без числа не
пишеться; список аргументів, прив'язка виходу і `415_type2` не змінюються; повтор - 0 змін; у лозі окремий рядок
`type1 без числа`). Функції `Get-LandParityBranchNullType1Component`, `Convert-LandParityBranchNullFormula`.

#### OptionalArguments: необов'язкові аргументи C11_03 / C11_10 / C11_11_03 / C11_16 (D-397)

Клас O з D-8: старий код НЕ передає аргумент для частини таблиць методології (атрибута AF-таблиці немає, `requiredFields`
його не містить) - ключа немає, формула лишається нерозв'язаною, результат не пишеться. Без колонки з таким кодом
`ECR-CALC-0438` не пускає публікацію. **NULL-міст (Formula-колонка з виразом `NULL`) не будується:** за
`LAND-D8-Q1-NULL-BRIDGE` він дав би `#ARG`, а не null. Натомість `-Stage OptionalArguments` (лише явно, в `All` не входить,
лише чернетки, не публікує; запускати ОСТАННІМ - після `Methodologies`, `CompositionMass`, `ParityBranch`) ставить у
`;`-списку аргументів формул методологій з моделі (`argumentBridges.optionalArguments`) маркер `?` на елементі:
`@Land_Measure_OperatingTime?`. Маркер знімає вимогу колонки (0438) для всієї версії, а відсутня комірка в рантаймі стає
null (як порожня). Перелік (11 пар методологія/таблиця): C11_03 - `Land_Measure_RunningHours` (T9_05, T9_14; Q-D8-2, дефолт «паритет зі старим кодом»: rename OperatingTime -> RunningHours старий код робить лише на 9a_17/9_06/9_09/9_13, тож на T9_05/T9_14 аргумент = null без нової версії шаблону); C11_10 - `Land_Measure_OperatingTime` (T9_02, T9_11, T9a_24, T9a_26),
`Land_Measure_SubstanceConsumption` (T9_07 - після K1, T9a_27, T9a_28); C11_11_03 - `Land_Measure_MaterialResidence` (T9_06); C11_16 -
`Land_Measure_FlowRateSulfur` (T9a_23). Клас K (конфлікт джерел) НЕ входить. Попередження публікації `publish.warning.optionalArgumentNoColumn` називає позначений
аргумент, якого немає в жодній прив'язаній таблиці (описка). Чиста логіка - `LandOptionalArguments.Lib.ps1` (тест
`LandOptionalArgumentsTests`); перелік веде `af_attr_bridges.py` (`OPTIONAL_ARGUMENTS`), повтор - 0 змін.
⚠ Маркер per-версія: на таблицях, де колонка є (напр. T9_07, T9a_27 для OperatingTime), відсутня комірка стає null
замість `#ARG`; перевірка паритету на стенді після «відбою» - PARITY_1 і `Compare-LandParityFull`.

#### ArgumentSuffix: суфіксовані аргументи C11_01 / C11_02 / C11_11_02 (K1)

Клас K з D-8: на T9a_17, T9a_27 і T9_07 методології однієї таблиці беруть один аргумент з РІЗНИХ атрибутів AF (старий
код перейменовує ключ у власному словнику), а колонка з кодом аргументу не може бути двома джерелами. K1: методологія-
«меншість» читає власну колонку-міст `<аргумент>__<код методології>` (`Land_Measure_OperatingTime__ECW_C11_01`,
`..._ECW_C11_02`, `Land_Measure_SubstanceConsumption__ECW_C11_11_02`), решта - незмінне ім'я. Model-gen (`af_attr_bridges.py`,
`ARGUMENT_SUFFIXES`, розділ моделі `argumentBridges.argumentSuffixes`) додає 16 Formula-колонок (14 суфіксованих по одній у
кожній таблиці методології + звичайні `Land_Measure_OperatingTime` на T9a_17/T9a_27); нічого не видаляє й не змінює.
`-Stage ArgumentSuffix` (лише явно, в `All` не входить, лише чернетки, не публікує; запускати ПІСЛЯ `Methodologies`,
`CompositionMass`, `SubstanceApplicability`, `ParityBranch` і ПЕРЕД `OptionalArguments`) підміняє `@<аргумент>` у виразах і
елемент у списках аргументів (кінцевий `?` і чужі елементи зберігаються) і додає суфіксований аргумент у PARITY_1 з тим
самим значенням; повтор - 0 змін. Чиста логіка - `LandArgumentSuffix.Lib.ps1`; тести `LandArgumentSuffixStageTests`,
`LandArgumentSuffixModelTests`. ⚠ Нові колонки існують лише в НОВІЙ версії шаблону (клон -> `-Stage Template` -> migrate),
а 0438 перевіряє прив'язки методології на всіх версіях шаблону: перед видачею перевірте (read-only), чи є прив'язки на
старих версіях (рішення K-eng-lite залежить від цього).

#### LookupCompare: порівняння Lookup-аргументу з текстом довідника (LAND-5094, діагноз A)

`-Stage LookupCompare` (лише явно, в `All` не входить, лише чернетки, не публікує, повтор - 0 змін; реалізація - lane
`lane/integ-land-lookup-compare`, `LandLookupCompare.Lib.ps1`). Навіщо: колонка-Lookup і колонка-міст Formula над Lookup
(`Land_Measure_ReleaseColdVent`, T10_01/T11_01) віддають у формулу НОМЕР запису довідника (189/190), тож
`@X = 'No - Нет'` ніколи не істинне (C12_03: 1 248 клітинок MISMATCH_LEGACY_ZERO). Етап переписує порівняння на
`REGFIELD(@X, 'NAME') = 'No - Нет'` (також `'lit' = @X`, `<>`, `!=`, `in(@X, 'lit', ...)`), лише коли літерал - ім'я запису
довідника цього аргументу (перелік - з колонок Lookup/Formula-над-Lookup моделі, REGFIELD-мости не чіпаються); маркер `?`
зберігається; у PARITY_1 аргументу виставляється числове `value` (id запису). Параметри: `-OnlyMethodology <коди>`.

#### NaeType: вибір type1/type2/type3 за NAE у C11_10 (D-423)

`-Stage NaeType` (лише явно, в `All` не входить, лише чернетки, не публікує, повтор - 0 змін; `LandNaeType.Lib.ps1`).
Старий код рахує лише тип, обраний за NAE (`ECW_C11_10.cs:180-184`, `:305-311`, `:328`); в ECR усі типи - виходи. Етап
обгортає вираз кожного типу `if(in(@Land_NAE, '<NAE типу>', ...), <вираз>, NULL)` і додає `@Land_NAE` у список аргументів.
Перелічені NAE з хвостом - лише `0647..0652` з ` (001-002)`; відрізання будь-якого хвоста після `(` (як `:133-141`) НЕ
відтворено (словник виразу не має рядкових функцій) - звірка на стенді. Параметри: `-OnlyMethodology <коди>`.

Порядок етапів: `ArgumentSuffix` -> `LookupCompare` -> `NaeType` -> `OptionalArguments` (OptionalArguments лишається
останнім: попередні етапи переписують вирази й списки, `?` ставиться на готовий список).

#### Часові межі DV на нечасових колонках (D-429)

`model-gen/gen_ecr_model.py` (`is_foreign_time_bound`) не створює правило `DV_<колонка>` (`SUM([col]) <= N`, N ∈ 28..31, 672, 696, 720, 744) і знімає `dv` колонки, якщо одиниця не `h` і не `d` (kg/h, Sm3, т, ppm, густина), колонка не прихована і в заголовку немає «days». Рішення людини 14.10: у xlsm межа годин накрила їх суцільним діапазоном. Втрачають правило 121 (13 груп: T9_04 ×24, T5_26 ×12, T6_11 FlowRateOfSulfur ×12, T10_04 FuelConsumption ×1, T9_06/T9a_16 SubstanceConsumption ×24, T9a_19 ×48); OperatingTime, дні і приховані службові `DV_C_*` лишаються. Модель перегенеровується лише `run-model-gen.ps1 -Work <власний каталог>`.

⚠ Правила НЕ відновлюються автоматично при міграції: у новій версії шаблону ці колонки лишаються без DV (правила, що вже є у версії-джерелі, міграція не додає назад). Порядок вікна 2: нова версія шаблону → `migrate-version` → R2 лише для документа 10 (`-DocMap {"LAND-PROD-6":10} -IncludeTables T9_04`; 868 рядків, що блокувались межею 744) → перерахунок 16 документів.

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

### C05_05 / T3_00: наявні ручні колонки як мости (P1-2, без нового D-nn)

`ECW_C05_05.cs:73-130` завжди бере `Density`, `FuelLowerHeating`, `MercaptanSulphurContent`, `H2Smass` зі складу HSE400
(рік, місяць, префікс потоку з `Land_TypeFuel`; немає запису - `continue`). На T3_00 ці колонки вже були ручним `Decimal`
(0 клітинок), тому `hse400_bridges.apply()` (працює лише зі списком `unresolved`) моста не створював і всі кроки C05_05
мали `#ARG`. `hse400_bridges.apply_existing_inputs()` (`EXISTING_INPUT_BRIDGES`) перетипує **ті самі** колонки на місці в
`Formula` (`readOnly`, `34/16`) з виразом `composition_expression(REGFIELD([Land_TypeFuel], 'NAME'), <показник>)`;
нових колонок немає, аргументи методології зв'язуються за кодом. У клоні шаблону перетип робить `-Stage Template`
(рядок «колонок переведено в обчислюваний тип», `Decimal/Int -> Formula/Calculated`); значень 0, тож `guardedWithData`
не спрацьовує. Сторож: LandHse400BridgesModelTests (`Чотири_аргументи_C05_05_на_T3_00_…`).

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
джерела потоку (`hse400_bridges.STREAM_ARGUMENT_ALIASES`). ✎ D-426/D-427 (2026-10-14): `REG*`/`CURRENT_SUBSTANCE()`/`SUBSTANCE_APPLICABLE` тепер ДОЗВОЛЕНІ й у `Legacy`
(код: `DialectCatalog.IsAllowedIn`; `Ln`/`ifs`/`CONVERT`/`SUBSTANCE` - далі лише Strict), у `Legacy`-версії, що читає
довідники, Lookup-аргументи йдуть з `EntryRef`, а NaN/±∞ маскується в 0 на КОЖНІЙ формулі (причина - `MaskedZeroReason`
у трейсі). Тобто паритет з легасі (ділення на 0 -> 0) для C11_10/C11_11_04/C12_01/C12_03 досягається НОВОЮ Legacy-версією
з тими самими формулами (`EffectiveFrom` за вікном стенда; опубліковані Strict-версії незмінні) - це крок вікна стенда,
не код. Інверсію умовчання етапу CompositionMass (`-KeepNumericMode`) робить окрема правка `Build-LandTemplate.ps1`.
Довідник `LAND_HSE400_SUBSTANCE` на стендах, побудованих до цієї зміни, заводить
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


## Формат відображення Decimal-колонок (LP-21)

Рішення людини 12.10: «Decimal 16 зберігаємо до 16 знаків, показуємо за `DisplayFormat` колонки». Модель
(`land-template.model.json`) несе `displayFormat` лише там, де xlsm має числовий формат домінантного стилю даних
колонки (ширше за 80 % рядків; порожні колонки й «General» формату не мають): 595 із 2304 Decimal-колонок.
Решта (1709: 1255 без заповнених рядків, з них 49 доданих ECR-колонок-аргументів без стовпця Excel; 454 із
рядками, але «General» або без домінантного стилю) отримує формат із правила `Resolve-LandDisplayFormat` (`LandTemplateDiff.Lib.ps1`), без зміни моделі:

| Колонка | `DisplayFormat` |
|---|---|
| має `displayFormat` у моделі (Excel) | він сам, без змін |
| Decimal без формату Excel, Scale ≥ 1 | `0.` + `0` × min(Scale, 6); для Scale 16 це `0.000000` |
| Decimal без формату Excel, Scale = 0 | `0` |
| не Decimal, Scale не задано | не виводиться |

`0.000000` - найчастіший формат самого Excel-шаблону (458 із 595; у 14 таблицях, де є і колонки з форматом, і без
нього, він домінує у 13). Виводити формат за «сусідніми» колонками таблиці не стали: 1286 без форматів стоять у
таблицях, де жодна Decimal-колонка форматів не має, а сусід-викид (`0.0`, `0.00`) переніс би чужий формат.
Build (`Stage Template`, презентація колонок) записує результат правила в `cfg.ColumnDef.DisplayFormat`
(ідемпотентно, `ISNULL`/порівняння), Verify-LandTemplate звіряє його з версією («презентація колонок»).
Тест: `LandDisplayFormatRuleTests` (Ecr.Architecture.Tests).

## Перенос значень застарілих таблиць 9a (D-1): `Move-LandStaleTableValues.ps1`

Після публікації нової версії шаблону БЕЗ `-PruneStaleTables` і `migrate-version` (режим Safe, без мапи колонок)
таблиці `T9a_03 / T9a_06 / T9a_07` лишаються в документах разом зі значеннями. Скрипт переносить ці значення в
`T9a_17a / T9a_21a / T9a_21b` окремим кроком ПІСЛЯ міграції (мапа таблиць — `rename-maps/land-9a-missing-tables.json`).

```powershell
$pw = Read-Host 'Пароль ECR' -AsSecureString
# 1. План (за замовчуванням лише читає; значення не друкуються, лише лічильники й коди):
.\Move-LandStaleTableValues.ps1 -ApiBase http://localhost:5094 -User admin -Password $pw -DocumentId 2 -PeriodKey 202606
# усі документи проєкту, усі 12 місяців року:
.\Move-LandStaleTableValues.ps1 -ApiBase http://localhost:5094 -User admin -Password $pw -ProjectId 1 -Year 2026
# 2. Запис (після Reopen аркушів людиною/координатором):
.\Move-LandStaleTableValues.ps1 -ApiBase http://localhost:5094 -User admin -Password $pw -DocumentId 2 -PeriodKey 202606 -Execute -ConfirmStand localhost:5094
```

- **Відповідність.** Таблиця - за мапою (на тому самому аркуші); рядок - за `rowKey` (він зберігається, як у
  `migrate-version`); колонка - за кодом колонки (запис мапи може мати `columns[{fromColumnCode,toColumnCode}]`).
- **Що рахується окремо й НЕ пишеться:** колонки немає в новій таблиці (код колонки друкується), колонка нової
  таблиці лише для читання (обчислювана), рядка немає в таблиці без динамічних рядків, таблиці-приймача немає.
- **Конфлікт** (у цільовій комірці вже інше значення) - пропуск і лічильник; перезапис лише з `-OverwriteConflicts`.
- **Ідемпотентність:** значення, що вже збігається, - «однакове»; повторний запуск після успіху пише 0 рядків.
- **Джерело** не змінюється; стирання перенесених/однакових значень у старій таблиці - лише з `-ClearSource`.
- **Reopen.** Аркуш у стані Submitted/Approved для запису треба відкрити (`POST /documents/{id}/reopen`). Скрипт
  Reopen НЕ виконує (це рішення людини/координатора). Якщо є що писати, а аркуш не відкритий, `-Execute` зупиняється
  ДО будь-якого запису: «потрібен Reopen аркуша X/період Y». У плані такі аркуші позначено рядками з «!».
- **Безпека:** лише локальний стенд (як решта скриптів: `Assert-LocalBaseUrl`); запис - лише з `-Execute` і
  `-ConfirmStand`; пейсинг не частіше 1 виклику на 2.2 с (`-PaceSeconds`), `Retry-After` поважається; значення
  комірок у вивід не потрапляють. PATCH іде від імені користувача входу з `origin = UserEdit`, тож права на
  комірки й вікно доступу діють як для людини (відхилені рядки рахуються в «відхилено», код виходу 1).
- Тест: `LandStaleValuesTests` (Ecr.Architecture.Tests; функції бібліотеки `LandStaleValues.Lib.ps1` виконує справжній
  PowerShell з підробленим Api).

## Вікно паритету: точкові інструменти (2026-10-13)

Раніше це були скрипти поза репозиторієм із шляхами, портом і базою репетицій. Тепер вони тут, без значень прода,
без секретів; тест — `LandWindowToolsTests` (Ecr.Architecture.Tests).

Спільні правила: `-BaseUrl` і `-ConfirmStand` **обов'язкові без дефолтів**; за замовчуванням (і з `-DryRun`) інструмент
нічого не пише; запис — лише `-Execute` разом з явним `-SqlConnection` (локальний сервер, база `EcrLand*`; потрібен для
назви бази в підтвердженні) і `-ConfirmStand <адреса>/<база>`; лише локальний API; облікові дані — `-User`/`-Password`
або `ECR_USER`/`ECR_PASSWORD`; `EcrApi.ps1` підключається через каталог скрипта.

| Інструмент | Що робить |
|---|---|
| `Load-LandInputCells.ps1 -Kind TypeFuel\|Gtu -SourcePath <каталог JSON> -DocMap '{"<ім'я>":<id>}'` | `Land_TypeFuel` і `ReleaseOnColdVentYesNo_M<мм>` (T10_01/03/04, T11_01) або GTU-входи (T10_03/T11_03) із JSON повного набору в документи. Документ береться з `-DocMap` (ім'я → id), не за назвою на стенді. JSON з прод-даними лишається поза git (`-SourcePath` — локальний шлях). |
| `Invoke-LandRecalc.ps1 -DocMap … -PeriodKey 202601,202602` | послідовний перерахунок документів із очікуванням задач; 429 чекає `Retry-After`. |
| `New-LandMethodologyDraft.ps1 -Methodology ECW_C12_03 -VersionNumber V2` | чернетка як клон останньої опублікованої (етапи Build працюють лише по чернетках); повтор — 0 змін. |

`Load-LandInputCells.ps1` перед записом робить **передперегляд** (потрібен `-User`): для кожного документа/періоду/таблиці
читає колонки й рядки та друкує `NO_DOC`, `NO_TABLE`, `ROW_MISSING`, лічильник комірок, пропущених через відсутню колонку
(`skipped-missing-column cells`), `lookupMiss` і **перелік відсутніх колонок** `таблиця/колонка` (без значень) — його
звіряють з мостами моделі. Код виходу 1 за будь-якого з них; `-Execute` у цьому разі зупиняється до першого PATCH.
Відсутні колонки пропускає лише явний `-AllowMissingColumns` (пишуться комірки наявних колонок, решта лишається в звіті).
Перелік GTU-колонок береться **по-таблично з моделі** (`T10_03`: Month + GTU1..GTU4 = 21; `T11_03`: Month + GTU1..GTU5 = 26),
а не зашитим списком. GTU-колонка файла, якої в моделі таблиці немає (GTU5 на T10_03), лічиться окремо як
`WARN source-without-target-column` (таблиця/колонка=кількість, без значень), не вантажиться й код виходу не змінює.
Колонка, яка є в моделі, але відсутня на стенді, як і раніше дає код 1. Ім'я документа з `GET /api/v1/documents/{id}`
(`nameL10n.values.en`) звіряється з ключем `-DocMap`: розбіжність — `NAME_MISMATCH`, код 1 (обхід — `-AllowNameMismatch`).
Так само в `Invoke-LandRecalc.ps1`. `Build-LandTemplate.ps1`: `-AccountsFile` без дефолту; без параметра береться
`<корінь репозиторію>\.sync-local\land-accounts.local.txt`.
Без `-User` друкується лише план за файлами (мережі немає), перевірки стенда не виконуються.

### Склад речовин для форми A

Форма B: категорії констант `{ключ}_{код речовини}` - склад версії виводиться з суфіксів `_(\d{3,4})$`. Форма A: старий
код шукає константу за категорією-ключем **без коду** (`ECW_C11_10`: 23 категорії `0220…0970`; `ECW_C11_11_05`: NAE), а
коди речовин ітерує зі своїх словників; суфіксів немає - склад з категорій порожній, `CURRENT_SUBSTANCE()` у формулах
дає `#REF`, результатів 0. `-Stage LandParity` тепер: (1) правило з `substancesFromApplicability: true` - завжди
об'єднання кодів `land-substance-applicability.json` (C12_03); (2) інакше, є суфікси - вони, без змін; (3) інакше -
об'єднання кодів словника застосовності цієї методології (`Resolve-LandMethodologySubstanceCodes`,
`LandSubstanceApplicability.Lib.ps1`). У журналі етапу - рядок «форма A … зі словника застосовності». Константи per-речовина
не потрібні: `ConstantResolver` пробує `{ключ}_{код}`, далі `{ключ}`. Методології без суфіксів і зі словником: C11_10
(15 кодів), C11_11_05 (330, 333). C11_01 - змішана (газ/LPG без коду, решта `_{код}`): береться суфіксна гілка; перевірте
склад на стенді (`calc.MethodologySubstance`) і, якщо коди газу відсутні, додайте правилу `substancesFromApplicability`.

NAE з хвостом у дужках на T9_04 (`0647 (001-002)` … `0652 (001-002)`): діалект не має рядкових функцій, тому правило
C11_10 перелічує обидва написання (з пробілом і без) у гілці групи `0647`, як C11_07/C11_11_02.

Порядок для чернетки форми A (методологія вже імпортована, константи є):

```powershell
# 1. правило категорії + склад речовин зі словника + аргументи PARITY_1
.\Build-LandTemplate.ps1 -Stage LandParity -OnlyMethodology ECW_C11_10 -BaseUrl … -ConfirmStand <адреса>/<база> -SqlConnection …
# 2. перевірити склад: GET /api/v1/methodologies/{id}/versions/{vid}/substances  (очікується 15 записів для C11_10)
# 3. PUT …/substances  (робить сам етап LandParity; вручну - лише коли довідника LAND_AIR_SUBSTANCE ще не було)
# 4. publish версії (роль-рецензент, effectiveFrom - рішення людини)
# 5. recalc документів: Invoke-LandRecalc.ps1 -DocMap … -PeriodKey …
```

Скорочено: «Stage LandParity → PUT substances → publish → recalc».
### ECW_C12_03 стає звичайним етапом Build

Правило категорії ECW_C12_03 тепер у `af-land-category-rules.json`
(`if(in(@IDCodeNAEProject,'0026','0030','0036','0038','0040','0042'),'0026','')`: на T10_01/T11_01 немає `Land_NAE`,
джерело його мосту — `IDCodeNAEProject`). Позначка `substancesFromApplicability` робить складом речовин версії
об'єднання кодів `land-substance-applicability.json` (15 кодів), а не суфікси констант. Порядок для чернетки:

```powershell
.\New-LandMethodologyDraft.ps1 -Methodology ECW_C12_03 -VersionNumber V2 -BaseUrl … -ConfirmStand … -Execute …   # чернетка
.\Build-LandTemplate.ps1 -Stage LandParity,SubstanceApplicability,CompositionMass -OnlyMethodology ECW_C12_03 `
    -BaseUrl … -ConfirmStand <адреса>/<база> -SqlConnection …                                                     # правило, 15 речовин, обгортка, фрагмент складу, Strict
```

`-OnlyMethodology` (етапи LandParity, CompositionMass, SubstanceApplicability) обмежує обробку заданими кодами;
без нього етапи, як і раніше, обходять усі методології. Вручну лишається аргумент `Land_TypeFuel` (запис довідника з
`entryId`) у PARITY_1: обгортка `REGFIELD(@Land_TypeFuel,'NAME')` читає Lookup, а підібрати запис довідника без
стенда етап не може. Публікація (роль-рецензент, `effectiveFrom`) — окреме рішення людини, інструменти її не виконують.

## Ключ рядка документа Land: `p<doc>-<TableCode>-<seq>` (LAND-5094, діагноз Б)

Старий ключ `p<doc>-<seq:0000>` повторювався в кожній таблиці документа, а `calc.CalculationResult` має лише
`DocumentId` + `SourceRowKey` (без таблиці), тому harness не міг сказати, якій таблиці належить результат, і писав
його всім таблицям-кандидатам («останній виграє»). Тепер ключ має код таблиці: `p10-T3_00-0001`.

- Генерація й розбір обох форматів: `LandRowKey.Lib.ps1` (`New-LandRowKey`, `ConvertFrom-LandRowKey`).
- Loader вхідних комірок: `Load-LandInputCells.ps1 -RowKeyFormat Table` (за замовчуванням `Legacy`; формат
  має збігатися з ключами рядків на стенді, інакше `ROW_MISSING`).
- Harness `Compare-LandParityFull.ps1` читає обидва формати. Ключ з кодом таблиці - результат ЛИШЕ цієї таблиці.
  Старий ключ при кількох таблицях-кандидатах із різними значеннями -> клас `AMBIGUOUS_ECR_KEY` (група `other`,
  значення не порівнюється і не друкується). Формат ключів документа визначається з рядків стенда.
  `summary.json` (formatVersion 3, без змін): нові поля `ambiguousEcrKeys {keys, byMethodology[]}` і
  `rowKeyFormats {table, legacy, unknown}`.
- **TableInstanceId у harness (K1-діагностика п.B).** SQL-вибірка результатів бере `calc.CalculationResult.TableInstanceId`
  -> `cfg.TableDef.Code`; ключ порівняння - `(table, rowKey)`, а не голий `rowKey`. Легасі-ключ `p<doc>-<seq>`
  без коду таблиці колізував між T9_02/T9_04/T9_07 (`BRANCH_BOTH_PRESENT`, `AMBIGUOUS_ECR_KEY`); тепер результат з
  `TableInstanceId` належить рівно своїй таблиці. Для `TableInstanceId IS NULL` - стара поведінка
  (`Resolve-LandParityResultTables`). У summary: `resultsWithTableInstance` / `resultsWithoutTableInstance`
  (разом з `rowKeyFormats`). Офлайн-стан: поле результату `table`.
- **MISSING_BINDING_INACTIVE (P3).** Методологія Published, прив'язки до таблиць є, але УСІ `IsActive=0` - окремий
  клас від `MISSING_NO_BINDING` (справжня відсутність прив'язки); група `oracleOnly`; у summary
  `missingBindingInactiveCells`. `Verify-LandTemplate.ps1` ловить причину: Published остання версія методології
  моделі без активної прив'язки -> розбіжність (`-InactiveBindingsAsWarning` - лише попередження).
- **C12_01 при порівнянні:** `-TypeFoldMethodology '^ECW_C12_01$' -GroupBy 'T11_02=Land_TypeFuel'` (і T11a..d) `-AggregateBy NaePhase`.
  `AGG_ECR_NON_RUNNING` більше не ставиться за самою наявністю не-Running члена групи: лише коли на не-Running
  рядку Є результат ECR (раніше 980 клітинок були артефактом).
- Міграція тестових документів: перезавантажити рядки новим ключем і зробити один recalc (вікно K1); доки
  стенд має старі ключі, harness працює в режимі Legacy і чесно позначає колізії.

## Loader рядків: `Load-LandRows.ps1` (LAND-5094)

Завантажує рядки ПОВНОГО набору (`LAND-PROD-<n>.json`, поза репозиторієм) у документи локального стенда; замінює
позарепозиторний `Load-Full.ps1`. Значень прода в git немає, вихід - лише лічильники й коди.

```powershell
# Передперегляд лише з файлів і моделі (без -User стенд не читається):
.\Load-LandRows.ps1 -SourcePath <каталог JSON> -DocMap '{"LAND-PROD-7":12}' -BaseUrl http://localhost:5094 -ConfirmStand localhost:5094
# Передперегляд зі читанням стенда (NO_DOC / NO_TABLE / колонки / довідники) і запис:
.\Load-LandRows.ps1 ... -User land-owner -Password $pw -SqlConnection 'Server=localhost;Database=EcrLandUi;Integrated Security=true' `
    -ConfirmStand localhost:5094/EcrLandUi -Execute
```

- Прапорці: `-BaseUrl`, `-ConfirmStand` обов'язкові без дефолтів; без `-Execute` (або з `-DryRun`) нічого не пишеться;
  запис вимагає `-User` і явного `-SqlConnection`; `-PeriodKey`, `-BatchRows`, `-Out`, `-ModelPath`,
  `-RowKeyFormat Table|Legacy` (дефолт `Table`).
- rowKey: `p<doc>-<TableCode>-<seq:0000>` - унікальний між таблицями документа (див. розділ про ключ рядка вище).
- Кап: за замовчуванням НЕМАЄ (`-MaxRowsPerTable 0`). Старий кап (498 із 788 у T3_00, 184 із 368 у T9_04) був числом
  рядків Excel-шаблону (`excel.rowCount`), а не `MaxDynamicRows` (1000 / 500); явний `-MaxRowsPerTable N` лічиться як `capped`.
- Мапінг колонок: спершу `prodColumnMapping` моделі (аліаси, місячні колонки з нерегулярними кодами: T9_04
  `SubstanceConsumptionKg/KgH`, T11c/d `No_*` -> `FB_*No`), далі вбудовані аліаси
  (`Land_Measure_ReleaseColdVent`->`ReleaseOnColdVentYesNo`, `Land_Measure_NumberOperations`->`NumberOfOperationsN`,
  `Land_NAE`->`IDCodeNAEProject|IDCodeMPEProject` (T6_12), `Land_Agent_*`/`Land_TrappingEfficienty`->довгі `K1…/B1…/DustTrappingEfficiency`,
  `Land_TypeFuel`->`TypeOfFuel|FuelType`), далі те саме ім'я, `_M<mm>`, `…Ref`. Формульні (readOnly) колонки ціллю не бувають.
- Лічильники: `unmapped` (колонка джерела без цілі), `sourceWithoutColumn` (вхідна колонка шаблону без джерела: нічого
  не вигадується), `missingColumns` (аліас моделі веде на колонку, якої немає в таблиці), `lookupMiss`, `capped`.
- Код виходу 1: `NO_DOC`, `NO_TABLE` (невідома таблиця прода / таблиці немає в документі), `MISSING_COLUMN`, `NAME_MISMATCH`,
  відхилені рядки; запис зупиняється до першого PATCH, якщо передперегляд має знахідки.
- Не завантажуються вода/відходи (`AF_Water_Land_07/07a`, `AF_Waste_Land_08`): ключі за елементами шаблону;
  лічаться як `SKIPPED_WATER_WASTE`. Заголовок документа й створення документа теж не портовано (id беруться з `-DocMap`).
- Перезавантаження після зміни ключа/мапінгу (LAND-PROD-6, T6_12) - у K1-вікні, одним recalc.
- Стан NAME_MISMATCH (ім'я документа на стенді інше, ніж ключ -DocMap) блокує документ і дає exit 1; обхід лише явним `-AllowNameMismatch`.
- Невідомі таблиці прода дають NO_TABLE (exit 1) і блокують -Execute. Явно й не за замовчуванням: `-SkipUnknownTables` (пропуск із WARN і лічильником SKIPPED_UNKNOWN) та `-IncludeTables 'T9_04,T9_07'` (лише ці коди таблиць; решта NOT_INCLUDED).

## Селектор гілки у паритеті (C11_01 / C11_02 / C11_11_02 / C11_11_03)

Легасі рахує ОДНУ гілку виходу на (рядок, вихід) за NAE і потоком; ECR віддає всі. Раніше harness складав гілки в один ключ
(«last wins») і давав `BRANCH_BOTH_PRESENT`. Тепер (`LandParity.Lib.ps1`, `Get-LandParitySelectedBranch`; продукт не змінено):

- значення зберігаються по гілках (`EcrVar`); порівнюється лише гілка, яку обрав би легасі; решта гілок - клас
  `ECR_ONLY_NOT_SELECTED` (група OTHER, не ECR_ONLY і не BRANCH_BOTH); у summary `branchSelectedCells` / `notSelectedCells`;
- методологія з селектором знімає гілку без `-TypeFoldMethodology`; вимкнути - `-NoBranchSelector` (тоді fold лише за
  `-TypeFoldMethodology`, кілька гілок = `BRANCH_BOTH_PRESENT`);
- проміжні `SumXiDiv*` (C11_11_02) - `ECR_ONLY_INTERMEDIATE` (як `_NOx`).

Правила вибору (джерело - docs/source, ECW_C11_OPF_Plant_Operational):

| Методологія | Правило | Джерело |
|---|---|---|
| C11_01 | NAE (без пробілів) містить 6646..6657 -> `type2`; інакше потік FG/LPG -> `type1_1`; Diesel/452 -> `type1_2`; решта -> `type1_3`. Потік: `Land_TypeFuelOrStream` (інакше `Land_TypeFuel`) | ECW_C11_01.cs:105-119, 176-208 |
| C11_02 | лише за NAE, точний збіг зі списком: type1 (6640..6645, 6582(002), 6585/6586/6587/6592 з хвостами), type2 (6783(001), 6786(001), 6787(001), 6789(001)); NAE поза списком - легасі результату не дає | ECW_C11_02.cs:143-146, 357-361 |
| C11_11_02 | NAE 0883 або група 6975 (6975, 6976, 6981..6986) -> `type2`; інакше `type1`; NAE поза groupNAE - результату немає | ECW_C11_11_02.cs:109-133, 221-239 |
| C11_11_03 | окремі групи: tons `type1/type2/type3`, gsec `type1/type2` (0804, 6801..6803 у gsec - це `type2`). Гілки tons і gsec НЕ змішувати: `type3` існує лише в tons | ECW_C11_11_03.cs:81-112 |

Застереження: гілка береться з рядка поточного члена групи (`AggregateBy`); для C11_11_02 category1 легасі бере з
`naeNumberObj` - тут використано той самий NAE рядка. Модельні розбіжності (немає tons у C11_01/C11_02, tons C11_11_03 без
`type2`) селектор не ховає: вони лишаються `MISSING_OUTPUT`.

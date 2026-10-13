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

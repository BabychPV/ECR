# Методології як скрипт: генеральна репетиція на корпусі з репозиторію (18.11.2026)

План: `docs/build/METHODOLOGY-SCRIPT-PLAN.md`, розд. 8. Інструмент: `tools/Ecr.MethodologyScriptMigration` (`convert`, `shadow`, `parity`, `pack`).
Гілка `lane/ms-r-corpus`. База: власна `EcrMsCorpus` (локальний SQL Server), API на порту 5092.

Це репетиція на **тому корпусі, що є в репозиторії**, а не на копії прода. Усе, чого в репозиторії немає, названо нижче в розд. 3 і не вигадувалось.

## 1. Корпуси: що знайдено

| Корпус | Де | Методологій / версій | Формул | Констант (кодів / значень) | Завантажено в `EcrMsCorpus` |
|---|---|---|---|---|---|
| Land (AF Air, пакет `ecr-methodology-package` v1) | `docs/delivery/reference-data/land-template/af-land-package.json` | 12 методологій (Common + ECW_C05_01..05, C06_01, C07_07, C07_07_01, C07_08, C07_16, C07_21), по 1 версії `V1` | 49+21+13+12+10+8+16+22+4+47+9+7 = 218 кодів | Common 275/281, C05_01 29/330, C05_02 16/30, C05_03 12/12, C05_04 18/36, C05_05 15/15, C06_01 9/27, C07_07 20/69, C07_07_01 14/27, C07_08 27/70, C07_16 10/18, C07_21 17/47 | так, через `POST /api/v1/methodologies/import` |
| HSE400 (рівень `Module`) | `tools/hse400/hse400-model.json` + `Build-Hse400Template.ps1` | 1 методологія `HSE400`, 1 версія `1.0.1.0` (чинна з 2025-01-01) | 369 | 224/224 | так, штатним `Build-Hse400Template.ps1 -Stage All` (шаблон, методологія, публікація із золотими тестами, прив'язки, проєкт, документ) |
| HSE30x, HSE401 | `tools/hse30x`, `tools/hse401` | методологій немає: це формули-колонки шаблону (`Kind = Formula` у шаблоні), не `calc.Methodology` | — | — | поза обсягом перенесення |
| Сіди | `src/Ecr.Infrastructure/Persistence/Sql/seed` | методологій у сідах немає (лише рядки локалізації з словом «methodology») | — | — | після `setup-dev-db.ps1` у `calc.Methodology` 0 рядків |
| Air: решта ECW_C** (C01–C04, C08–C12 тощо) | немає в репозиторії | — | — | — | **неможливо**: потрібне вивантаження AF XML (`Methodologies\…`; `docs/source/` у репозиторії відсутній) і `tools/Ecr.MethodologyImport export`, тобто доступ до PI/AF. Залишено на сесію з реальним вивантаженням |

`tools/Ecr.MethodologyImport` запису в БД не робить (лише `analyze/export/unresolved`); у базу пакет потрапляє через API `POST /api/v1/methodologies/import`.

## 2. Результати

Усього версій у базі: 13 (12 Land + 1 HSE400).

### 2.1 convert

| Корпус | Версій | Перенесено | Відмов | Причини відмов |
|---|---|---|---|---|
| Land: Common (бібліотека) | 1 | 1 | 0 | — |
| Land: ECW_C05_01..05, C06_01, C07_07, C07_08, C07_21 | 9 | 9 | 0 | — |
| Land: ECW_C07_07_01, ECW_C07_16 | 2 | 0 | 2 | `missingRows` (обидві) |
| HSE400 (Module, 369 формул) | 1 | 1 | 0 | — |
| **Разом** | **13** | **11** | **2** | |

Попередження після остаточної побудови: лише в двох відмовних версіях (C07_07_01: 11 пар `constantAmbiguous`+`constantUnresolvable`; C07_16: 8 пар). Решта версій — без попереджень (крім інформаційного `formulaOrderDerived` у неопублікованої бібліотеки Common).

### 2.2 shadow

Записано 11 з 11 текстів, 0 невдач. Для бази з іменем без слів `copy|test|upg|dev|stand` потрібен `--i-know-this-is-a-copy` (це власна база репетиції).

### 2.3 parity

Команда порівнює лише опубліковані версії рівня `Configuration` з тіньовим текстом (`EfParityTargetSource`).

| Версія | Екземплярів таблиць (11 періодів із даними + 1 порожній) | Рядків | Значень | Розбіжностей |
|---|---|---|---|---|
| ECW_C05_01 | 12 | 176 | 594 | 0 |
| ECW_C05_02 | 12 | 176 | 396 | 0 |
| ECW_C05_03 | 12 | 176 | 1826 | 0 |
| ECW_C05_04 | 12 | 176 | 330 | 0 |
| ECW_C05_05 | 12 | 176 | 264 | 0 |
| ECW_C06_01 | 12 | 44 | 528 | 0 |
| ECW_C07_07 | 12 | 44 | 132 | 0 |
| ECW_C07_08 | 12 | 33 | 132 | 0 |
| ECW_C07_21 | 12 | 33 | 198 | 0 |
| **Усе разом (`parity` без фільтра)** | **108** | **1034** | **4400** | **0** |
| HSE400 | не порівнюється | — | — | — (див. 2.4) |
| Common, C07_07_01, C07_16 | не порівнюються (бібліотека; не опубліковані й без прив'язок) | — | — | — |

Контроль чутливості: у тексті C05_03 константу `k1_ECW_C05_03_` змінено з 0.8 на 0.81, `shadow` + `parity` дали 297 розбіжностей (`value.number`); після повернення оригіналу — знову 0. Тобто нулі вище не є порожнім порівнянням.

### 2.4 Що не вдалося порівняти: HSE400

`parity` не перевіряє версії рівня `Module` (`EfParityTargetSource.ListShadowedVersionsAsync` відбирає `Level == Configuration`), хоча план, етап 7, каже «Паритет — на документі HSE400». Конвертація й запис тіньового тексту (124 184 байти) для HSE400 пройшли без зауважень, але **збіг чисел старого й нового модуля місячних колонок інструментом не доведено**. Це прогалина інструмента (не дефект конвертера), класифікація (в/пропуск етапу 7): потрібна окрема команда або розширення `ParityRunner` для `MonthColumnArgumentsModule`. У цій сесії не реалізовано.

## 3. Класифікація відмов і ризиків

| № | Що | Клас | Деталі |
|---|---|---|---|
| 1 | `ECW_C07_07_01`, `ECW_C07_16`: `missingRows` | (б) дані корпусу | У пакеті немає ні прив'язок до таблиць, ні правил рядків: прив'язки колонок результатів створює `Build-LandTemplate.ps1` зі свого переліку `$AF_TABLE`, де цих двох методологій немає (у `PublishList` теж; у коді збирача вони згадані як «додані на live пізніше»). Без прив'язки немає речення `rows from …` (план 8.2: «Прив'язки → rows from таблиці»). Для переносу прода дані мають прийти з бази, де прив'язки є |
| 2 | Шість методологій без правила категорії (C05_02, C05_04, C06_01, C07_07, C07_08, C07_21) давали попередження `constantAmbiguous`/`constantUnresolvable` (75 пар), поки правила категорій не додано | (б) дані корпусу | У пакеті `categoryRule` порожній; ці вирази живуть у `Build-LandTemplate.ps1` (`$CAT`), а `af-land-category-rules.json` містить лише C11/C12. Те саме, що в плані, розд. 14 («63 константи неоднозначні через відсутні правила категорій — це дані пакета»). Після додавання правил (розд. 4) попереджень немає |
| 3 | Версії-чернетки без `EffectiveFrom` → `convert` пише «Skipped: draft without EffectiveFrom» (12 з 12 до публікації) | (в) свідоме обмеження | Імпорт AF дає чернетки; конвертер працює з версіями, що мають дату чинності. План, 8.1: «версії рівня `Configuration` … »; на проді версії опубліковані |
| 4 | Версія без рядків правил → `rowsNoRules`; без прив'язок → `missingRows` | (в) | Конвертер відмовляється, а не вгадує вибір рядків (план 8.2, «чого конвертер не вміє — пише у звіт») |
| 5 | `parity` не охоплює `Module` | прогалина інструмента | розд. 2.4 |

Дефектів конвертера чи рушія, що відтворюються на цьому корпусі, **не виявлено**: коду й тестів у `tests/Ecr.MethodologyScriptMigration.Tests` не змінено.

## 4. Чого репетиція не охопила (і чому)

Повна побудова Land (`tools/land/Build-LandTemplate.ps1`, 83 таблиці, 5 145 колонок, довідники, акаунти, `RowFilter`, правила з `entry()`) **не запускалась**: скрипт навмисно приймає лише бази з іменем `EcrLand*` (`Assert-LocalSqlConnection`), а для репетиції дозволена лише `EcrMsCorpus`. Охорону не обходили. Замість нього побудовано мінімальний шаблон `LAND_REH` (5 таблиць T3_00, T4_01, T5_08, T5_09, T5_27 із колонками-аргументами й колонками виходів, одна версія 1.0.1.0 після клону 1.0.0.0), тому:

- речення `rows`: перевірено лише рядкові правила на колонках `String` (C07_21: 2 правила `Land_TypeBurnFuel`, C07_08: 1 правило `Land_TypePetrStation`) і порожнє правило `{}` (інші); гілка `entry("REG","CODE")` для Lookup-колонок і `active when row.<статус> == entry(…)` (`Methodology.RowActivityFilter`, статус-колонки) **не виконувались**;
- правила категорій, які в Land читають `REGFIELD(@Land_TypeFuel,'NAME')`, замінено еквівалентами на текстових колонках (`@Land_TypeFuel = 'Diesel'`, ...); `!ECW_Category` для C05_02 і C07_21 взято з пакета як є;
- прив'язки: усі активні `matchJson` різні (прив'язка до якоря таблиці з кодом методології), бо шаблон не публікується з однаковими предикатами на одній колонці; для паритету це не впливає (береться таблиця прив'язки);
- дані паритету синтетичні: на таблицю на місяць 16 (T3_00) або 4 рядки, значення — золоті тести методологій × коефіцієнти 0.5…2.0 і місячний множник (січень–листопад; грудень закритий політикою періодів, `PeriodNotOpen`), плюс по рядку з неповним вводом (перевірка `MissingArguments`); реальних значень Land немає;
- Land-версії опубліковано штатним потоком (`Calculation.Publish` другим користувачем, золоті тести з `Build-LandTemplate.ps1`, `effectiveFrom 2024-01-01`); усі 9 золотих тестів пройшли на **старому** рушії;
- перерахунок документів і `script-migration/apply|switch` не виконувались (етап 8 — лише за словом людини).

Втручання в базу поза API (лише власна `EcrMsCorpus`):

1. На початку `UPDATE calc.MethodologyVersion SET EffectiveFrom='2024-01-01'` для 12 чернеток, щоб `convert` взагалі їх побачив; пізніше публікація з тією ж датою це перекрила.
2. Два правила C07_08/C07_21 були записані мовою PowerShell 5.1 без BOM у спотвореному кодуванні (кирилиця в літералі), через що `parity` порівнювала 0 рядків. Виправлено прямим `UPDATE calc.MethodologyRule.MatchJson` (Id 9, 10), тексти перетворено і записано заново. У скриптах репозиторію (`rehearsal/*.ps1`) додано BOM — цю правку повторним прогоном **не перевірено**.

## 5. Як відтворити

Передумови: SQL Server на `localhost` (Windows-автентифікація), .NET 10, `dotnet restore Ecr.sln`. Порт 5092 вільний. Усі команди з кореня worktree; `MS_REHEARSAL_DIR` — тека поза репозиторієм (паролі й проміжні файли).

```powershell
$env:MS_REHEARSAL_DIR = 'C:\...\rcorpus'          # поза репозиторієм
$cs = 'Server=localhost;Database=EcrMsCorpus;Trusted_Connection=True;TrustServerCertificate=True'

# 1. База (~15 хв на завантаженій машині: збірка + DataGen)
powershell -File tools\setup-dev-db.ps1 -Server localhost -Database EcrMsCorpus -Documents 1 -SmallFiles -DataPath F:\ecr-wt\upgdata-ms

# 2. API на 5092 (у передньому плані окремим вікном або Start-Process; наприкінці ЗУПИНИТИ)
$env:ECR_ConnectionStrings__Ecr = $cs
$env:ECR_Bootstrap__Password = 'Dev-Bootstrap-2026!'
$env:ASPNETCORE_URLS = 'http://localhost:5092'
dotnet run --project src\Ecr.Api --no-build --no-launch-profile

# 3. Акаунти (bootstrap -> роль MsCorpusOwner з усіма правами -> ms-owner) і імпорт пакета Land
#    (bootstrap не має Calculation.EditFormula, тому потрібен окремий користувач)
powershell -File tools\Ecr.MethodologyScriptMigration\rehearsal\01-api-import.ps1

# 4. Шаблон LAND_REH (spec: rehearsal\land-spec.json; генерується rehearsal\make-land-spec.py), виходи й прив'язки v1, правила v1
powershell -File tools\Ecr.MethodologyScriptMigration\rehearsal\02-land-lean-template.ps1
powershell -File tools\Ecr.MethodologyScriptMigration\rehearsal\03-land-bind-v1.ps1
powershell -File tools\Ecr.MethodologyScriptMigration\rehearsal\04-land-rules-v1.ps1

# 5. Шаблон v2 (колонки для правил категорій), проєкт LAND_REH_2026, документ, правила категорій,
#    золоті тести, публікація (другий користувач ms-reviewer), активні прив'язки
powershell -File tools\Ecr.MethodologyScriptMigration\rehearsal\05-land-template-v2-and-publish.ps1

# 6. Дані Land (січень–листопад 2026) і HSE400
powershell -File tools\Ecr.MethodologyScriptMigration\rehearsal\06-land-data.ps1
#   HSE400 штатним будівником (файл облікових даних: рядок "ms-owner<TAB>пароль" поза репозиторієм):
powershell -File tools\hse400\Build-Hse400Template.ps1 -Stage All -Year 2026 -BaseUrl http://localhost:5092 -Database EcrMsCorpus -ConfirmStand EcrMsCorpus -CredentialFile $env:MS_REHEARSAL_DIR\cred.tsv
powershell -File tools\Ecr.MethodologyScriptMigration\rehearsal\07-hse400-data.ps1

# 7. Інструмент перенесення
dotnet build tools\Ecr.MethodologyScriptMigration
& dotnet run --project tools\Ecr.MethodologyScriptMigration --no-build -- convert --connection $cs --out $env:MS_REHEARSAL_DIR\out
& dotnet run --project tools\Ecr.MethodologyScriptMigration --no-build -- shadow  --connection $cs --in  $env:MS_REHEARSAL_DIR\out --i-know-this-is-a-copy
& dotnet run --project tools\Ecr.MethodologyScriptMigration --no-build -- parity  --connection $cs --out $env:MS_REHEARSAL_DIR\parity-all.json
& dotnet run --project tools\Ecr.MethodologyScriptMigration --no-build -- parity  --connection $cs --out $env:MS_REHEARSAL_DIR\parity-C05_03.json --methodology ECW_C05_03
& dotnet run --project tools\Ecr.MethodologyScriptMigration --no-build -- pack    --connection $cs --in  $env:MS_REHEARSAL_DIR\out --out $env:MS_REHEARSAL_DIR\package.json
```

Примітки до відтворення:

- Скрипти `rehearsal/*.ps1` — ті самі, що виконувались, зі зміною лише шляхів (`MS_REHEARSAL_DIR`, відносні шляхи до пакета й моделі HSE400) і BOM у кодуванні; після зміни повного прогону не було. Порядок 03/04 → 05 збережено як у прогоні: 05 клонує v1 і вимикає успадковані прив'язки перед публікацією v2.
- `convert` до публікації версій (чернетки без дати) нічого не перенесе — спершу кроки 3–5.
- `parity` порожній (код виходу 1, «НІЧОГО ПОРІВНЮВАТИ»), поки немає опублікованих версій із тіньовим текстом, документів із рядками й активних прив'язок.
- Код виходу `parity` 255 у циклі PowerShell з `| Select-Object -First 1` — наслідок закриття каналу, а не помилка інструмента.

## 6. Висновок

- На корпусі з репозиторію (Land 12 версій, HSE400 1 версія) конвертер перетворив 11 з 13; 2 відмови — дані корпусу (немає прив'язок до таблиць).
- Паритет на 9 версіях Land: 108 екземплярів таблиць, 1034 рядки, 4400 значень, 0 розбіжностей; мутація тексту виявляється.
- HSE400: конвертація й запис тіньового тексту є, **паритет не доведено** (інструмент не підтримує `Module`).
- Решта Air-корпусу в репозиторії відсутня: для неї потрібне вивантаження AF.
- Повна побудова Land і правила з `entry()`/`RowActivityFilter` не перевірялись; їх покривають лише модульні тести конвертера.

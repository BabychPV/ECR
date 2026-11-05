# tools/hse401 — шаблон HSE401 і один документ 2026 на scratch-стенді

Призначення: перевірити сценарій «користувач змінив формулу в шаблоні» на реальному файлі `HSE401_v1.0.3.1_patched 2.xlsm`
(6 428 рядків × 62 колонки). Файл лише читається (копія в `%TEMP%`), VBA не читається і не запускається. Python не потрібен.
За замовчуванням (`-Target Scratch`) лише scratch-база `EcrHse401Scratch`, API `http://localhost:5180` (охорона в `Hse401Stand.Lib.ps1`; порти 5094/6094 і чужі бази заборонені).
Живий стенд — окремий режим `-Target Live` з явним підтвердженням, див. розділ «Живий режим» нижче.
Виходи (`out/`) і паролі (`artifacts/hse401-accounts.tsv`) у git не потрапляють.

## Запуск (з нуля, ~5 хв)

```powershell
powershell -File tools\hse401\Start-Hse401Stand.ps1 -Action Create     # DROP+CREATE бази, -SmallFiles, дані на F:\EcrData
powershell -File tools\hse401\Start-Hse401Stand.ps1 -Action Start      # API :5180, PID у artifacts\hse401-api.pids
powershell -File tools\hse401\Run-Hse401All.ps1                        # витяг -> довідники -> NAE -> шаблон -> проєкт -> завантаження -> порівняння
powershell -File tools\hse401\Test-Hse401FormulaEdit.ps1               # клон -> формула -> publish -> migrate -> перерахунок -> перевірка
powershell -File tools\hse401\Start-Hse401Stand.ps1 -Action Stop       # зупинка лише за власними PID
```

| Скрипт | Крок |
|---|---|
| `Extract-Hse401.ps1` (+`Hse401Xlsx.Lib.ps1`) | словники, вхідні комірки, кеш Excel, ознаки формул, база робочих днів по рядку |
| `Build-Hse401Registries.ps1` | `LAND_AIR_SUBSTANCE` (+`COMPONENT`, `PHASE`), `HSE401_*` (8 списків; значення рядків, яких нема у словнику, додаються як «зайві» `<PFX>Xnn`) |
| `Import-Hse401Nae.ps1` | дерево NAE нашим Excel-імпортом (analyse → preview → apply) |
| `Build-Hse401Template.ps1` | шаблон, 27 груп заголовків (до 2 рівнів), 32 формули, publish; проєкт Yearly 2026 і документ |
| `Load-Hse401Document.ps1` | PATCH пачками по 1000 рядків (`r0001…`), округлення шуму float (лог `out/hse401-roundings.csv`) |
| `Compare-Hse401.ps1` | порівняння з кешем Excel: пусто = 0, відн. допуск 1e-9, класи розбіжностей |
| `Test-Hse401FormulaEdit.ps1` | сценарій зміни формули (×1.1 у `H_RATE`) і заміри |

## Результат прогону (04.11.2026, порожня база → документ)

* Книга: 6 428 рядків, 404 964 комірки (177 404 формули в Excel); 633 джерела (регіон | суша/море | MPE), 85 речовин.
* Завантажено: 144 610 вхідних комірок за 7 пачок; PATCH 27,6 c чистого часу (54 c із підготовкою JSON); перерахунок завершився через 6,5 c після останнього PATCH.
* Усе в базі: 292 421 значення комірок; зріз таблиці 6 428 рядків = 9 329 КБ, 3,7–4,1 c.
* NAE: 633 з 633 SRC розміщено (REGION 2, AREA 3, SITE 14, EQUIP 50), помилок імпорту 0; 4 джерела мають у книзі кілька шляхів «район | об'єкт» (NAE стоїть на першому).
* Порівняння (302 116 комірок, 47 колонок): збігів 295 743; tolerance 5 622 (|Δ| ≤ 1e-13, але відносно > 1e-9: дрібні ліміти, див. нижче);
  gs-rule 344; phase-vocabulary 158; expected-literal 155; mixed-basis-row 94; **REAL 0**.
* Зміна формули (`H_RATE` ×1.1): клон 1,0 c, PUT 0,03 c, publish 0,13 c, migrate dry-run 0,6 c, migrate-задача 20,2 c (перенесено 292 421 значення),
  разом migrate + перерахунок до тиші 28,4 c; перевірено 102 848 комірок тонн — усі = 1.1 × старе; RQ без змін.

## Висновки / знахідки

1. База робочих днів не лише 183/247: **111** (1 592 рядки, Excel-рядки 4553–6297), 183 (25), 247 (242), решта = днів у році.
2. У рядках з базою 247 Excel рахує `BE..BL` за днями в році, а в рядках з 111 — за 111; шаблон бере `BASIS` для обох → 94 комірки `mixed-basis-row`.
3. Зберігання комірки `decimal(34,16)`: проміжна колонка `H_RATE` і дрібні ліміти (< ~1e-5 т/рік) втрачають відносну точність (до ~1e-8); абсолютна похибка ≤ 1e-13.
4. `PATCH /documents/{id}/cells` повертає `rowVersions` для ВСІХ рядків таблиці (148 КБ при зміні однієї комірки в таблиці з 6 428 рядків).
5. 38 значень у рядках книги відсутні в її словниках (4 тексти: 2 райони, 1 об'єкт, 1 тип обладнання) — додані як «зайві» записи списків.

## Живий режим (`-Target Live`)

Ті самі скрипти (`Run-Hse401All.ps1`, `Build-Hse401Registries.ps1`, `Import-Hse401Nae.ps1`, `Build-Hse401Template.ps1`, `Load-Hse401Document.ps1`,
`Compare-Hse401.ps1`) уміють працювати з живим локальним стендом (напр. API `http://localhost:5094`, база `EcrLandUi`). Без `-Target Live` усе
поводиться як раніше (scratch, охорона `:5180`).

**Правило репетиції.** Спершу — на КОПІЇ живої бази, піднятій на ІНШОМУ порту (власний API, база-копія з іншим ім'ям); лише коли репетиція чиста
(`-PreviewOnly`, потім повний прогін, потім повторний прогін без змін), той самий набір команд запускається на живому стенді. Живий стенд ніколи не є першою
ціллю нового скрипта чи нової версії скриптів.

**Що змінюється в Live.** Немає `Create/Start/Stop/DROP` (`Start-Hse401Stand.ps1` лишається лише для scratch), немає `Initialize-Hse401Accounts`,
bootstrap, створення ролей і користувачів (крок `Accounts` пропускається). Вхід — з файлу облікових даних. Мутації вмикає лише
`Set-EcrStandConfirmation` (`хост:порт/база`) після перевірки `-ConfirmStand`. Стан прогону пишеться окремо (`out/hse401-state.live.json`,
`out/hse401-nae-map.live.json`, `out/hse401-nae-tree.live.xlsx`) і має мітку стенда: файл від іншого стенда відхиляється.

### Параметри

| Параметр | Значення |
|---|---|
| `-Target Scratch\|Live` | за замовчуванням `Scratch` |
| `-BaseUrl` | Live: `http://localhost:5094` (лише локальні адреси: `localhost`, `127.0.0.1`, `[::1]`) |
| `-Database` | Live: ім'я бази, напр. `EcrLandUi` |
| `-ConfirmStand <база>` | Live: має дорівнювати `-Database`, інакше скрипт зупиняється до будь-якого виклику |
| `-CredentialFile <шлях>` | Live: TSV, один рядок `користувач<TAB>пароль`; файл лише читається (не пишеться, пароль не потрапляє в лог); шлях усередині git-worktree (або під коренем репозиторію) відхиляється |
| `-PreviewOnly` | обидві цілі: лише `GET` + NAE `analyse`/`preview`; будь-який інший виклик кидає виняток у `Invoke-Hse`/`Invoke-HseRawJson`/NAE-завантаженні; на вході не ставиться підтвердження стенда (другий рубіж) |
| `-AcceptNaePreview` | Live: прийняти помилки/колізії кодів у NAE preview (див. нижче). Вилучені вузли НЕ обходить |

### Команди

```powershell
$cred = 'D:\secrets\hse401-live.tsv'          # поза репозиторієм; рядок: hse401-loader<TAB>пароль

# 1) лише читання: план у консоль і out\hse401-preview.json
powershell -File tools\hse401\Run-Hse401All.ps1 -Target Live -BaseUrl http://localhost:5094 -Database EcrLandUi -ConfirmStand EcrLandUi -CredentialFile $cred -PreviewOnly

# 2) повний прогін (після репетиції на копії і після погодженого плану)
powershell -File tools\hse401\Run-Hse401All.ps1 -Target Live -BaseUrl http://localhost:5094 -Database EcrLandUi -ConfirmStand EcrLandUi -CredentialFile $cred

# окремі кроки - ті самі параметри цілі, напр. лише довідники
powershell -File tools\hse401\Build-Hse401Registries.ps1 -Target Live -BaseUrl http://localhost:5094 -Database EcrLandUi -ConfirmStand EcrLandUi -CredentialFile $cred
```

### План `-PreviewOnly`

Консоль і `out/hse401-preview.json` (розділи `registries`, `nae`, `template`, `project`, `load`): довідники до створення; поля до додавання по довідниках;
записи до створення по довідниках (коди); **кожне заплановане заповнення COMPONENT/PHASE окремо** (довідник, код запису, поле, нове значення); NAE-рівні
(створити/існує) з помилками й колізіями кодів з preview; збіги з вилученими вузлами; чи існують шаблон `HSE401`, проєкт `HSE401_2026`, політика
`HSE401_OPEN`, документ; кількість вхідних комірок, які пішли б у PATCH (і скільки рядків у документі вже є: PATCH створює рядки з `baseVersion=null`,
тож непорожній документ дасть 409).

### Дозаповнення COMPONENT/PHASE (ідемпотентність)

`Build-Hse401Registries.ps1` для НАЯВНИХ записів `LAND_AIR_SUBSTANCE` (за кодом, напр. `E101`) заповнює `COMPONENT` і `PHASE` лише там, де вони порожні:
`GET /registries/LAND_AIR_SUBSTANCE/entries/{id}` → `POST /registries/LAND_AIR_SUBSTANCE/entries` з `id`, `code`, батьком як є, порожнім `display.values`
(назва зливається, а не замінюється) і ЛИШЕ полями, що заповнюються (значення полів зливаються: решта не чіпається). Нічого не створюється, інші поля не
змінюються. Токена конкуренції (`If-Match`/`rowVersion`) цей ендпоінт не має; пишуться тільки порожні поля, тож гонка нічого не затирає. У лог іде кількість
дозаповнених і вже заповнених значень. Помилка запису одного запису не зупиняє решту, але прогін завершується винятком із переліком.

### NAE: вилучені вузли і колізії

* **Вилучені вузли ніколи не чіпаються** (ні відновлення, ні перенесення, ні правка полів). Сервер зіставляє наявні вузли за кодом разом із вилученими,
  тому перед `apply` скрипт (у БУДЬ-ЯКОМУ режимі, також у `-PreviewOnly`) звіряє файл з `NAE_TREE`: вузол вважається вилученим, якщо `validTo <= сьогодні`
  (їх видно зрізами `GET .../entries?asOf=<минула дата>`: 1900-01-01 і початки кварталів від 2010) або `IsActive=false` (у переліку їх не видно:
  ловляться розбіжністю `preview.existing` рівня `SRC` з кількістю чинних вузлів із кодами файлу `N_<РЕГІОН>_<СУША/МОРЕ>_<MPE>`). Збіг з вилученим кодом SRC
  чи з назвою вузла групового рівня того самого рівня друкується списком, і `apply` НЕ виконується. `-AcceptNaePreview` цього не обходить.
  Обмеження: назви групових вузлів (REGION/AREA/SITE/EQUIP) порівнюються за англійською назвою (best-effort); вузол, вилучений на вузьке вікно між
  квартальними зрізами, ловиться лише для SRC (через лічильник).
* У Live будь-яка помилка/колізія в NAE preview (`codeTaken`, `existingElsewhere`, `duplicateInFile`, ...) зупиняє роботу ДО `apply`, якщо не вказано
  `-AcceptNaePreview` (з ним проблемні рядки, як і в scratch, виключаються з файлу, решта застосовується).

### Мінімальні права

Визначено за кодом сервера (контролери й обробники). Усі права — глобальні; окремо для запису комірок потрібен рівень доступу, а не код права (останній рядок).
`-PreviewOnly` потребує лише читальних прав зі стовпця (`View`, `Document.View`, `Project.Manage` для списку політик, `Registry.EditData` для NAE analyse/preview).

| Крок / ендпоінт | Право |
|---|---|
| `POST /login/local` | — |
| `GET /registries`, `/registries/{код}/definition`, `/registries/{код}/entries[?level=&asOf=]`, `/registries/{код}/entries/{id}` (і зрізи `NAE_TREE`) | `Registry.View` |
| `POST /registries` (лише якщо довідника ще немає) | `Registry.EditDefinition` |
| `PUT /registries/{код}/definition` (додати поля; лише якщо їх бракує) | `Registry.EditDefinition` + `Registry.Publish` |
| `POST /registries/{код}/entries` (створення записів, дозаповнення COMPONENT/PHASE) | `Registry.EditData` |
| `POST /nae-tree/import/analyse`, `/preview` | `Registry.EditData` |
| `POST /nae-tree/import/apply` | `Registry.EditData` (+ `Registry.EditDefinition`, якщо реєстру `NAE_TREE` ще немає) |
| `GET /templates`, `/templates/{id}/versions`, `/template-versions/{id}/structure` | `Template.View` |
| `POST /templates`, `POST /templates/{id}/versions`, `PUT /template-versions/{id}/sheets, tables, columns, formulas, header-groups`, `PATCH /template-versions/{id}/presentation` | `Template.Edit` |
| `POST /template-versions/{id}/publish` | `Template.Publish` |
| `GET /projects/period-policies`, `POST /projects/period-policies`, `POST /projects`, `POST /projects/{id}/activate` | `Project.Manage` |
| `GET /projects`, `GET /projects/{id}/periods`, `GET /documents`, `GET /documents/{id}/tables[/{ti}]` (зріз для Compare/перегляду) | `Document.View` |
| `POST /documents` | `Document.Create` (+ запис у проєкт `HSE401_2026`) |
| `GET /jobs?limit=50` (очікування перерахунку після Load у `Run-Hse401All.ps1`) | `System.ViewHealth` (без нього крок очікування впаде 403; дані вже записані) |
| `PATCH /documents/{id}/cells` | не код права, а рівень `Write` на аркуш `HSE` документа в проєкті (автор документа / призначення ролі проєкту) за `EditRules.CanEdit`; перевірте на репетиції |

Мінімальний набір кодів: `Registry.View`, `Registry.EditDefinition`, `Registry.Publish`, `Registry.EditData`, `Template.View`, `Template.Edit`,
`Template.Publish`, `Project.Manage`, `Document.View`, `Document.Create`, `System.ViewHealth` (+ рівень `Write` на аркуш для PATCH комірок).
Роль `SystemAdministrator`/«усі права» не потрібна й не створюється скриптами: роль і користувача для завантаження створює адміністратор стенда до запуску.

### Залежність від сервера

Скрипти читають записи `NAE_TREE` за рівнем (`GET /registries/NAE_TREE/entries?level=SRC`) і використовують `LookupFilter` `level=SRC` у колонках шаблону.
Потрібен сервер із комітом **33e23b50d** (фільтр `LookupFilter level=SRC` і `GET entries?level=`); на старішому сервері параметр `level` ігнорується або
відхиляється, і мапа NAE/колонки шаблону будуть неправильні. Перевірте версію на копії до репетиції.


## Оновлення вже завантаженого стенда: Update-Hse401Template.ps1 (05.11)

`Build-Hse401Template.ps1` наявного не перестворює, тож зміни моделі (колонка Excel L = `H_NAE` «MPE number / ПДВ номер», `H_MPE` прибрано,
назви «Emission Source Type» / «Work Days», формати показу R/S `0.000000000000` і BC/BD `0.0000`) на живу версію цей скрипт доносить сам:
клон опублікованої версії -> PATCH presentation (назви, формати, порядок, `IsHidden`) -> publish -> migrate-version документів проєкту HSE401_2026
(dry-run для всіх; будь-який `canApply=false` зупиняє) -> тиша перерахунку -> `Compare-Hse401.ps1` (REAL = 0, інакше код виходу 3).

**Прибрана з моделі колонка (`H_MPE`) за замовчуванням ХОВАЄТЬСЯ** (`IsHidden=true`), значення лишаються. Видалення колонки з даними Safe-перенос
відхиляє (`canApply=false`, `refusals:["dataLoss"]`), а опублікована версія незворотна - стенд застряє. Видалення лишилось лише за явним
`-DeleteRemovedColumns` (з гучним попередженням); тоді до clone/publish скрипт дивиться `GET /documents/{id}/tables/status?periodKey=` і за будь-якого
`rowCount > 0` зупиняється ДО publish. `-HideInsteadOfDelete` - застарілий порожній ключ (сумісність).

**Повторний запуск - порожня операція.** Версію документа скрипт порівнює рядком: `templateVersion` з `GET /documents/{id}` (напр. `1.0.0.3`) проти `version`
останньої опублікованої з `GET /templates/{id}/versions`; «схована колонка, якої немає в моделі» вважається виконаною; `422 ECR-TMPL-0422 migrateSameVersion`
трактується як «вже перенесено», не помилка.

**Файл стану обов'язковий** (`out\hse401-state.json`, для Live `out\hse401-state.live.json`; ключі `templateId, versionId, sheetDefId, tableDefId, columnIds,
projectId, documentId, tableInstanceId`). Його пише `Build-Hse401Template.ps1`. На стенді, де документ уже є, а файла немає, **не збирайте його руками**:
`-RebuildState` знаходить усе з API за кодами (шаблон `HSE401`, проєкт `HSE401_2026`) і записує файл з міткою стенда (у `-PreviewOnly` - лише в пам'яті).

Порядок (Live; спершу репетиція на копії, див. «Правило репетиції»). `<ціль>` = `-Target Live -BaseUrl <url> -Database <база> -ConfirmStand <база> -CredentialFile <файл>`:

    # 0) свіжий витяг книги (потрібен кроку NAE і зворотному заповненню)
    powershell -File tools\hse401\Extract-Hse401.ps1                      # -> out\hse401-extract.json
    # 1) шаблон: лише GET, звіт різниці; стан збирається з API, якщо файла немає
    powershell -File tools\hse401\Update-Hse401Template.ps1 <ціль> -RebuildState -PreviewOnly
    # 2) застосування (ті самі параметри без -PreviewOnly); H_MPE буде схована
    powershell -File tools\hse401\Update-Hse401Template.ps1 <ціль> -RebuildState
    # (лише для порожнього документа, свідомо:) ... -DeleteRemovedColumns
    # 3) NAE: нові вузли (display = "<MPE> — <назва>")
    powershell -File tools\hse401\Import-Hse401Nae.ps1 <ціль>
    # 4) NAE: наявні вузли SRC (лічильники без запису; потім із запису)
    powershell -File tools\hse401\Backfill-Hse401NaeDisplay.ps1 <ціль> -PreviewOnly
    powershell -File tools\hse401\Backfill-Hse401NaeDisplay.ps1 <ціль>
    # 5) офлайн-перевірка запобіжників і Pester
    powershell -File tools\hse401\Test-Hse401Guards.ps1 ; Invoke-Pester -Path tools\tests

(у PowerShell `$T` не розгортається в окремі аргументи - підставте параметри цілі прямо в команди.)

### NAE: показ вузла SRC

Колонка `H_NAE` - Lookup на `NAE_TREE level=SRC`, сітка показує `entry.display`. Display вузла SRC = `<MPE> — <назва>` (`<MPE>` - точний текст колонки L Excel, регістр
джерела; назва = MPE або порожня -> просто `<MPE>`), як у `LAND_AIR_SUBSTANCE` (`Format-Hse401NaeDisplay` в `Hse401Nae.Lib.ps1`, ідемпотентно, без подвійного префікса).
Нові вузли: `Import-Hse401Nae.ps1` кладе це в `Name_En`. Наявні: `Backfill-Hse401NaeDisplay.ps1` - лише чинні SRC (вилучені в переліку відсутні й не чіпаються), MPE береться з
витягу книги за кодом вузла (`N_<РЕГІОН>_<СУША/МОРЕ>_<MPE>`; вузол поза витягом - хвіст коду, рахується окремо). Перейменування -
`POST /api/v1/registries/NAE_TREE/entries` з `id` вузла, `registryDefId`, `code` і `levelCode` як є, **`parentEntryId` обов'язково як є** (інакше вузол піде в корінь),
`display.values` лише змінені мови (назва зливається по мовах), `values = {}`; нового часового рядка немає, окремої «версії» - теж (ревізія запису + подія аудиту).
Другий запуск - 0 змін. `Compare-Hse401.ps1` звіряє L з хвостом КОДУ вузла, не з display; клієнтське правило вставки приймає частину до « — ».
Формат показу (`ColumnDef.DisplayFormat`, до 50 знаків) - довільний рядок формату Excel: ExcelExporter ставить його в NumberFormat як є; грамліки
й валідації немає (є лише межа довжини). Сітка клієнта його не читає (там правило `Scale` <= 6 = доповнення нулями), тож формати R/S/BC/BD діють на експорт.
Compare для колонки L порівнює текст Excel із хвостом коду вузла SRC (`N_<РЕГІОН>_<СУША/МОРЕ>_<MPE>`) зі збереженням нулів зліва.

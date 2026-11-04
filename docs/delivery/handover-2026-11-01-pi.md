# Хандовер: PI / Event Frames / ручні значення / useUrlState (D-461…D-465 + аудит)

Дата: 2026-11-01. Автор: Claude (сесія 3eaaf5e6). Адресат: інший Claude, що продовжує роботу над ECR.

---

## 0. TL;DR

- **У `main` усе злито**: PR #658–#665. `main` = `ffce0e5d4`. Ліній (lanes) у роботі немає. Від цієї роботи не лишилося робочих копій, гілок і тестових БД.
- **Що з'явилось**:
  - ручне значення замість формули (D-461);
  - виправлений збір PI (D-462);
  - сторінка «Дані з PI» (D-463);
  - стійкий збір подій EF для річних періодів (D-464);
  - перегляд подій EF і заборона мапити EF на місячні колонки (D-465);
  - виправлення `useUrlState`;
  - виправлення 13 знахідок незалежного аудиту.
- **Відкриті питання** — лише такі, що потребують живого PI або рішення власника продукту (PO); див. §7.
- **Перед будь-якою роботою** прочитай рядки D-461…D-465 у `docs/tz/10-decisions.md` (там же нотатки «✎» від аудиту) і цей файл.

---

## 1. Контекст і стійкі правила користувача (обов'язково)

- Користувач — власник продукту (PO). Пише українською, **відповідати українською**.
- Користувач працює асинхронно. Він очікує, що ти продовжуєш сам і зливаєш, коли все зелене.
  - **Стійкий дозвіл на merge RC PR у main**, коли все зелене: «мердж роби коли все буде працювати і одразу кажу тобі так».
  - Звіти агентів ≠ дозвіл користувача.
- Тести запускаються **тільки в GitHub CI**. Локально дозволені лише вузькі запуски: один клас/фільтр, lint, tsc, окремі vitest-файли.
- **Факти — лише з коду**, нічого не вигадувати. Звіти субагентів перевіряй у коді для ключових тверджень. Я так знайшов, що висновок «e2e упав через тайм-зону» правильний лише для одного з двох тестів.
- Git:
  - ніколи `git add -A`, `--force`, `--no-verify`, «голий» `git stash`; **ніколи force-push**;
  - не вбивати чужі процеси;
  - чужі гілки/сесії видаляти лише з дозволу.
- Коміти закінчуються рядком `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Опис PR закінчується рядком `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.
- Email користувача — лише для атрибуції.
- Пам'ять проєкту: `C:\Users\User\.claude\projects\D--Own-project-ECR\memory\`. Актуальний стан PI — у `ecr_pi_followup_queue.md`. Індекс — `MEMORY.md`.

---

## 2. Процес доставки (як я працював — повтори його)

Репозиторій: `BabychPV/ECR`. Локальний клон: `D:/Own project/ECR/project/BabychPV/ECR`. Робочі копії ліній — у `F:/ecr-wt/<name>`.

1. **Лінія (lane):**
   ```bash
   git -C "D:/Own project/ECR/project/BabychPV/ECR" fetch origin
   git -C "D:/Own project/ECR/project/BabychPV/ECR" worktree add F:/ecr-wt/<name> -b lane/<name> origin/main
   ```
   Паралельні агенти — кожен у своїй робочій копії (worktree), з непересічними файлами.
2. **Push `lane/<name>`.** На `lane/**` `ci.yml` запускає лише вузькі джоби. Дочекайся зеленого.
3. **Повний CI.** Той самий SHA пушиться в `lane/integ-<name>`, там іде повний CI.
4. **Повний e2e:**
   ```bash
   gh workflow run e2e-stand.yml -R BabychPV/ECR -f ref=<SHA>
   ```
   5 шардів. У `gh run view` поле `headSha` показує main (ref воркфлоу), а не твій SHA. Це нормально.
5. **Перевірка оновлення БД** — якщо зачеплено міграції, SQL-скрипти чи сид (шаблон у §8). Якщо змінено **вже злиту** міграцію, перевіряй оновлення з двох баз:
   - з поточного main;
   - з бази, старішої за ту міграцію (`git worktree add --detach F:/ecr-wt/baseNN <старий SHA>` + `dotnet restore`).
6. **RC і PR:**
   ```bash
   gh api repos/BabychPV/ECR/git/refs -f ref=refs/heads/rc/<дата>-pNN -f sha=<SHA>
   gh pr create -R BabychPV/ECR --base main --head rc/<дата>-pNN ...
   ```
   Нумерація: останній — **p96**, наступний — **p97**. RC PR пропускає повні client/server джоби; e2e на PR при цьому біжить.
7. Дочекайся `mergeStateStatus` = `CLEAN` або `UNSTABLE`. `UNSTABLE` = e2e на PR ще біжить, але той самий SHA вже пройшов повний e2e. Тоді:
   ```bash
   gh pr merge N -R BabychPV/ECR --merge
   ```
8. **Прибирання:**
   - ff локального main;
   - `git worktree remove`;
   - видалити локальні гілки;
   - `git push origin --delete lane/<n> lane/integ-<n> rc/<...>`;
   - видалити свої тестові БД.
9. Якщо `origin/main` зрушив під час роботи лінії — `git merge origin/main` у лінію (без rebase) **до** фінальних прогонів повного CI/e2e.

**Пастки середовища:**
- Foreground `sleep` заблокований. Чекай через фонову команду (`run_in_background`) з until-циклом і **не лишай циклів після завершення**. Одного разу цикл висів 12 год, і користувач це помітив.
- Логи упалої джоби:
  ```bash
  gh run view --job <ID> -R BabychPV/ECR --log-failed
  ```
  `gh api .../logs` часто віддає 502.
- Великий repo: замість Grep-інструмента використовуй `git grep`.
- `dev/integration` застарілий, не використовується.
- `EcrTest_Api_*` БД із випадковими суфіксами — невідомого власника, не чіпай.

---

## 3. Що злито (хронологія)

| PR | Merge SHA | Що |
|---|---|---|
| #658 (p89) | 75c99e9af | **D-461** ручне значення замість формули |
| #659 (p90) | dd8c6ee81 | **D-462** виправлення збору PI |
| #660 (p91) | c1f67182e | Фікс режиму «на весь екран» аркуша (`SheetWorkspace.tsx`, ref + useLayoutEffect) |
| #661 (p92) | 70754d98c | **D-463** «Дані з PI» + «Звідки це значення» |
| #662 (p93) | c243f2bbe | **D-464** стійкість EF / RTQP для річних періодів |
| #663 (p94) | 332946e80 | **D-465** перегляд подій EF + заборона EF → місячна колонка |
| #664 (p95) | 767fed9de | `useUrlState` від живої локації + Esc у `EditAssignmentsDrawer` |
| #665 (p96) | ffce0e5d4 | Виправлення за незалежним аудитом D-461…D-465 |

---

## 4. Ключові факти домену (від PO)

- **Звітні періоди PI — річні.** Усе має працювати для року.
- Продакшн отримує дані переважно через **RTQP (PI SQL Client, ODBC)**: запити Event Frames / Elements, які повертають таблиці, як View у MS SQL. Адмін пише SQL-текст, система його транслює, PI повертає рядки.
- Пояс проєкту — **`Asia/Atyrau` (UTC+5)**. Період відкривається о 00:00 місцевого часу, тобто о 19:00 UTC останнього дня попереднього місяця.
- **Рішення PO щодо EF і місячних колонок — варіант A**: «ми обробляємо кожну строку методологією».
  - Кожна подія EF — окремий рядок `EF-{EventId}`. Місячні підсумки рахують формули шаблону.
  - Автоматичної розкладки EF → M1…M12 **немає й не має бути**.

---

## 5. Дизайн по кожному D (коротко, з файлами)

### D-461 — ручне значення замість формули
**Рішення PO:**
- окреме право;
- обов'язкова причина;
- Delete повертає формулу;
- прапорець вмикається на опублікованій версії без нової версії;
- вимкнення прапорця **лишає** наявні ручні значення;
- цілі формул рядка у вводних колонках лишаються редагованими («варіант А, 10 колонок: 5 для вводу, 5 для формул»);
- автор при відновленні міграцією — «Система»;
- потрібен фільтр ручних клітинок.

**Реалізація:**
- Таблиця `doc.CellFormulaOverride`: PK `(PeriodKey, TableRowId, ColumnDefId)`, без FK, партиціонована `ps_ByPeriodKey`.
- Право `Document.OverrideFormula`, прапорець `AllowManualOverride` на формулі.
- Основні файли: `RecalculationService.cs` (`IsManualTarget` пропускає), `FormulaOverrideStore.cs`, `FormulaManualOverrideHandler.cs`, `PatchCellsHandler.FormulaOverride.cs`, `EditRules.cs`.
- Позначки читаються лише коли версія дозволяє ручні значення або вже має позначки: `AnyForVersionAsync` через `IMetadataCache.GetDerivedAsync`, із bump `PresentationRevision` при перемиканні.

**Після аудиту (#665):**
- MERGE запису клітинок у `NormalizedCellStore.UpsertAsync` **не перезаписує** `IsCalculated = 1` поверх клітинки з позначкою.
  - Позначка читається з `READUNCOMMITTED`. Патч пише позначку до клітинки, а чекати на її блокування означало б deadlock.
- Нова позначка перевіряє прапорець у БД у тій самій команді: shared `HOLDLOCK` на рядку `cfg.FormulaDef`, не TemplateVersion — інакше deadlock з перемиканням.
- Відповідь «позначок немає» кешується ≤ 5 с (`shortLived` у `GetDerivedAsync`).
- Міграція версії документа, де в колонці зникла формула (`FormulaOverrideMigration.Unformulated`): позначка знімається, `IsCalculated = 0`, аудит з причиною `audit.reasonVersionMigrationNoFormula`.
- Імпорт Excel (`ImportDiffBuilder.cs`): клітинка, що збігається з відбитком експорту, **пропускається**, а не стає ручним значенням. Книги без відбитка (до AN-118) поводяться по-старому.
- `AnyForVersionAsync` ігнорує soft-deleted рядки.
- **Свідомо НЕ змінено:** архівація року (`arc.usp_ArchiveYear`) не чіпає `CellFormulaOverride`. Позначки лишаються на місці й підхоплюються при відновленні. Якщо їх обрізати без arc-дзеркала, ручні значення втратяться.
- Обмеження: в рідкісній гонці перерахунок може записати рядок аудиту про зміну, якої фактично не сталося.

### D-462 — збір PI
**Рішення PO:**
- коли видалено всі точки — клітинку очистити («інакше ми обманюємо користувача»);
- PI порожній за період, а в нас дані є — видалити в нас;
- закритий якірний місяць — варіант A: пишемо відкриті місячні колонки, закритий пропускаємо з подією;
- нарізка на шматки (chunking) визначається конфігом або самим запитом і не змінює звичайних прогонів;
- повернутий на редагування аркуш має отримати оновлені дані.

**Конфіг:**
- `Collection:ChunkDays` (7);
- `Collection:DeleteMissingPoints`;
- `PiSqlClient:CommandTimeoutSeconds` (60);
- стеля 600 тис. точок на прогін.

**Реалізація:**
- Після повернення аркуша ставиться матеріалізація: `SheetMaterializationTrigger`, `EnqueueAfterSheetReturnAsync`.
- Річна згортка в місячні колонки.

**Після аудиту (#665):**
- `CollectionRunner.Track`: якщо час будь-якої повернутої точки виходить за `[from, to)` шматка, видалення для шматка пропускається (ознака зсуву поясу) і пишеться попередження.
- `CollectionStore.DeleteMissingRawPointsAsync` пише внутрішній маркер `PointsRemoved` в `itg.CollectionCoverage`. Клітинка очищається лише якщо вікно порожнє **і** маркер є. Інакше — NoData, клітинку не чіпаємо.
- Новий конфіг `Collection:DeleteMissingOnSecondary` (типово false): відповіді резервного сервера PI нічого не видаляють. Описано в `docs/admin/operations-runbook.md`.
- PI Web API:
  - відповідь без `Items` — збій;
  - якщо є нерозібрані позначки часу або всі елементи `Good:false`, ставиться `DeletionUnsafe` (добрі точки зберігаються, але нічого не видаляється).
- Якщо прогін упав після запису даних, `CollectionJob` все одно ставить матеріалізацію `[from, to]` (крім auth і cancel).

### D-463 — «Дані з PI»
- Сторінка `/admin/pi-data`.
- API `/api/v1/sources/{id}/raw-data/{tags,points,series,coverage,export.csv}` (`Integration.View` або `Integration.Manage`).
- Інспектор клітинки: `/documents/{id}/tables/{instanceId}/cell-source[/points|/series]`.
- Спільні `Period.FoldBounds` і `CollectedFold`.
- Стани порівняння: Match / ManualEdit / NotUpdated / NotWritten / NotComparable / TooManyPoints (стеля згортки 50 тис. точок).
- Стеля експорту: `Integration:RawDataExportMaxRows`.
- Міграція `20261031111816_D463RawDataReadIndexes`. Після #665 індекс `IX_CollectionCoverage_PeriodEvents` будується через **raw SQL з `ONLINE = ON`** на редакціях 3/5/8, з перевіркою `INDEXPROPERTY` (за зразком `AN34CollectionCoverageDedupIndex`).

### D-464 — EF / RTQP
- Конфіг:
  - `PiSqlClient:EventQueryTimeoutSeconds` (300);
  - `PiSqlClient:EventQueryChunk` (None|Month);
  - `Collection:MaxEventPages` (10; при досягненні — `Truncated`, видалень немає).
- `WithConnectionAsync` (lease з'єднання).
- `SourceEventSyncTrigger`: синхронізація подій одразу після повернення аркуша чи відкриття періоду.
- Дія планувальника `SheetLocked`.
- Ключі `eventSheetLocked`, `eventOpenAtPeriodClose`, `eventPeriodChanged`, `eventsTruncatedCap`.
- `SourceEventSyncJob` щопрогону перечитує **весь** відкритий рік. `LookbackDays` / `ChunkDays` до EF не застосовуються. Період події визначається за StartUtc.
- **Після аудиту (#665):**
  - «Джерело порожнє» рахується **за власним шаблоном**, а не за об'єднанням із сусідніми.
  - Видалення **всіх** зв'язаних рядків прив'язки завжди вимагає підтвердження, незалежно від кількості.
  - Побічний ефект: якщо власний шаблон справді спорожнів, а сусідні щось повертають, рядки не видаляються навіть з підтвердженням. Це безпечний бік; прибирати вручну.
  - **API змінено:** `?confirmRemoval=true` прибрано. `POST …/source-events/sync` приймає тіло `{ confirmRemovals: [{ mapId, candidates }] }`; порожнє тіло — звичайна синхронізація.
    - Видалення понад ліміт — лише для підтверджених прив'язок і лише якщо поточних кандидатів ≤ підтверджених.
    - Невалідне тіло → 422 `err.ECR-INT-0422.eventRemovalConfirmInvalid`.
    - Повідомлення про завершення задачі несе `removalMaps` / `eventRemovalBlocked.perMap`.
    - Аудит `SourceEventRemovalConfirmed` містить прив'язки.

### D-465 — перегляд подій EF
- Таблиця `ext.SourceEventAttributeValue`:
  - ключ `(LinkId, Scope, Attribute)`;
  - число `decimal(34,16)` або текст ≤ 4000 з `IsTruncated`;
  - також UOM, `Modified`, `ChangedAt`, `RunId`, `ValueHash`;
  - без FK.
- Пише `SourceEventSyncJob`: один `MERGE` (OPENJSON) на 500 зв'язків, лише змінене.
- Міграція `20261031142719_D465SourceEventAttributeValues` + `IX_SEL_MapStart` (після #665 — ONLINE raw SQL).
- API:
  - `GET /sources/{id}/raw-data/events/maps`, `/events`, `/events/removed`, `/events/export.csv`;
  - `cell-source` повертає `eventSources[]` для рядків `EF-*`.
- Спільні `SourceEventRowBuilder` і `SourceEventFieldPlans.LoadAsync` — синк і перегляд рахують однаково.
- **Захист варіанта A:** мапінг поля EF на `IsMonthColumn` → 422 `err.ECR-INT-0422.eventMapMonthColumn`. Наявні такі мапінги лише позначаються:
  - `monthColumnFieldIds` (бейдж «Month columns»);
  - `hasMonthColumnFields`;
  - попередження синку в журналі сервера.
- Після #665: значення знятих зв'язків видаляються в тій самій транзакції, що `SaveChanges` (`SourceEventAttributeValueWriter.DropAsync`).
- e2e: `piEventsLive.spec.ts`, сид `tools/e2e/pi-events-seed.sql`, env `ECR_E2E_PI_EVENTS_ENTITY`. Сид чіпляє **призупинену** прив'язку до спільного документа стенду, тому видалення цього документа дасть 409.

### useUrlState (#664)
- Корінь: у react-router 7.18.3 `useSearchParams` передає в updater знімок `location.search` з останнього рендера. Через це друга швидка зміна відкочувала першу.
- `src/Ecr.Web/src/shared/ui/useUrlState.ts`: усі сеттери йдуть через `useApplyUrlUpdates`:
  - жива локація в ref (оновлюється в `useLayoutEffect`);
  - `navigate('?…', { replace: true })`;
  - модульний `pending`, прив'язаний до ідентичності location, щоб виклики в одному тіку компонувались.
- Сеттери тепер стабільні між змінами URL.
- Це виявило приховану помилку: Esc ловили і Modal майстра, і Drawer, тож закривались обидва. Фікс: `EditAssignmentsDrawer` має `closeOnEscape={!nestedOpen}`.
- **Правило на майбутнє:** шторка з вкладеним вікном, стан якого живе в URL, мусить вимикати `closeOnEscape`, поки вкладене вікно відкрите. Зразки: `GroupDrawer`, `RowDrawer`, `UserAccessEditor`.
- `DataSourceDrawer` (вкладені вікна в локальному стані), ймовірно, теж закривається по Esc. Це давнє, не змінювалось.
- Також закрито помилку в `PiDataPage.tsx`: `setEntityId` + `setTag(null)` відкочували вибір сутності.

---

## 6. Незмінні інваріанти, які легко зламати

- **Тести фіксованої кількості запитів** (`FormulaJobFixedCostTests`, `FormulaOverrideQueryCountTests`, `CellSourceApiTests`, стеля матеріалізації). **Стелі не піднімати.** Якщо зміна потребує більше запитів, перероби дизайн або спитай PO.
- `docs/build/02-contracts.md` охороняється `EndpointCoverageTests`. Нові роути, порти й ключі помилок мусять там бути. Також онови `openapi.snapshot.json` і `schema.d.ts`.
- `docs/tz/10-decisions.md` + `docs/CHECKSUMS.txt`. Чексума: `tr -d '\r' < file | sha256sum`.
- Сид `09-seed.sql`: en через MERGE, ru/kz у `#I18N`-секціях з назвами. Української локалі немає. kz — робочі переклади.
- Кількість прав у `SeedTests` змінюється, коли додаєш право. Колись це пропустили.
- `buttonScale`-guard: без `px` на Button, використовуй ActionIcon.
- Усі FK у моделі — Restrict (`DeleteBehaviorTests`).
- Бюджет бандла 250 KB на сторінку. Зараз PiDataPage 233.5, DocumentPage 242.6.
- Одна ексклюзивна міграція за раз (EF Core migration token).

---

## 7. Відкрите (потрібен живий PI / PO)

1. **Пояс RTQP.** `PiSqlClientDataSource.cs` (~1424–1450) вважає час без зони UTC; це явне неперевірене припущення. Звір одну позначку часу з PI SMT на справжньому DAS. Захист від хибного видалення вже є, але правильність самих даних залежить від цієї перевірки.
2. **Наявні мапінги EF на місячні колонки.** Порахувати на робочому інстансі (бейдж «Month columns») і переробити вручну.
3. **Події EF без зв'язку** (вперше побачені, коли аркуш був поданий) не видно в переліку — лише лічильник у журналі синку. Чи показувати їх — питання до PO.
4. **Подія EF, перенесена в PI на інший рік**, лишається `PeriodChanged`. «Відкрита на закриття періоду» видно лише в покритті.
5. **Перевірка на живому стенді** чекає даних замовника C-1…C-3.
6. **Флейк e2e** `submissionTargetLive.spec.ts:57` («submit Scheduled 202611» чекає 403, отримує 204) падає, якщо прогін іде близько 19:00 UTC останнього дня місяця. Помилка в тесті: календар береться до відкриття періоду. Кандидат на окремий маленький фікс тесту.
7. Сторонні робочі копії `D:/Own project/ECR/project/BabychPV/wt-audit2` (docs/rc17) і `F:/ecr-wt/an80stats` — не з цієї роботи, без дозволу не чіпати.
8. Інші паузні лінії, не з цієї роботи — див. пам'ять `ecr_r4_paused_lanes.md`.

---

## 8. Шаблон перевірки оновлення БД (PowerShell)

Збережи як `.ps1` і заміни `$cand` / `$db` / запит перевірки. Для старої бази заміни `$main` на detached-копію на старому SHA (і зроби `dotnet restore` у ній).

```powershell
$ErrorActionPreference = 'Stop'
$env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;" + $env:PATH
$main = 'D:\Own project\ECR\project\BabychPV\ECR'
$cand = 'F:\ecr-wt\<lane>'
$db = 'EcrUpg_<lane>'
Set-Location $main
powershell -ExecutionPolicy Bypass -File tools\setup-dev-db.ps1 -Server localhost -Database $db -Documents 1 -DataPath F:\EcrData
if ($LASTEXITCODE -ne 0) { throw "setup main $LASTEXITCODE" }
powershell -ExecutionPolicy Bypass -File tools\smoke.ps1 -Server localhost -Database $db -ExistingDatabase -AdminPassword 'Dev-Bootstrap-2026!' -Port 5097
if ($LASTEXITCODE -ne 0) { throw "smoke main $LASTEXITCODE" }
sqlcmd -S localhost -E -d $db -Q "EXEC sp_createstats;" | Out-Null
Set-Location $cand
dotnet restore Ecr.sln | Out-Null
powershell -ExecutionPolicy Bypass -File tools\setup-dev-db.ps1 -Server localhost -Database $db -Upgrade -DataPath F:\EcrData
if ($LASTEXITCODE -ne 0) { throw "upgrade1 $LASTEXITCODE" }
powershell -ExecutionPolicy Bypass -File tools\smoke.ps1 -Server localhost -Database $db -ExistingDatabase -AdminPassword 'Smoke-Real-2026!' -Port 5097
if ($LASTEXITCODE -ne 0) { throw "smoke cand $LASTEXITCODE" }
powershell -ExecutionPolicy Bypass -File tools\setup-dev-db.ps1 -Server localhost -Database $db -Upgrade -DataPath F:\EcrData
if ($LASTEXITCODE -ne 0) { throw "upgrade2 $LASTEXITCODE" }
# перевірка: новий індекс / таблиця / ключ сиду
sqlcmd -S localhost -E -d $db -h -1 -Q "SET NOCOUNT ON; SELECT 'idx', INDEXPROPERTY(OBJECT_ID('<schema.table>'),'<IX_name>','IndexID'); SELECT 'key', COUNT(*) FROM sys_ecr.UiString WHERE [Key]='<new.key>';"
sqlcmd -S localhost -E -Q "ALTER DATABASE [$db] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$db];" | Out-Null
'UPGRADE-OK'
```

Друге оновлення перевіряє ідемпотентність. Другий smoke використовує пароль, який задав перший smoke.

Запускати у фоні (`run_in_background`) з перенаправленням виводу в лог.

---

## 9. Як я делегував (що спрацювало)

- **Одна задача — один субагент у своїй робочій копії.** У брифі давай:
  - мету;
  - файли й рядки;
  - рішення PO дослівно;
  - процес із §2;
  - заборону створювати PR і зливати;
  - формат фінального звіту.
- **Аудит:** окремий read-only агент на кожну область. Відтворювальні тести (repro) — у тимчасовій робочій копії, без push. Знахідки — з `file:line` і вердиктом SAFE / BUG / RISK.
- **Перед виправленням** покажи знахідки PO і отримай «так».
- Ключові твердження агентів перевіряй у коді сам (одна-дві команди).
- Коли `main` зрушив, повідом агента лінії зробити `git merge origin/main` перед фінальними прогонами.

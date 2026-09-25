# UX-PASS, четвертий раунд — передача роботи (2026-09-25)

⛔ **Станом на 2026-09-25 (пізніше того самого дня) більшість розділів
нижче застаріли**: паралельна сесія продовжила цей самий бэклог і влила
майже все в `dev/integration` під іншими хешами комітів (той самий зміст,
інший автор коміту/rebase-шлях). Кожен пункт нижче звірено окремо напряму
в `git log`/`git diff`/коді `origin/dev/integration` — перекреслені
підтверджено реалізованими, неперекреслені лишаються дійсно відкритими.

Роботу призупинено на прохання людини. Тріаж і ID знахідок (F-/B-/R-/X-) —
`docs/build/UX-PASS-2026-09-23.md`, розділ «Четвертий раунд». Цей файл —
що лишилося і з чого продовжувати.

## Стан `dev/integration`

Влито (кожна гілка — після rebase і повного прогону всіх серверних наборів +
tsc/eslint/vitest): B2 (імпорт/фон/періоди, без F-13), D (шаблони й
довідники), E1 (документ і сітка), A (цілісність і права), хвіст B-08.
PR у `main` — #463, мерж раз на тиждень по семи зелених гейтах.

## Невлиті гілки (запушені окремо, НЕ в dev)

| Гілка | Комітів | Стан | Що зробити |
|---|---|---|---|
| `ux/r4-perf` | 3 | ~~готова; міграція `20260925024920_B18HotPathIndexes` (остання в ланцюжку); ризик `QUOTED_IDENTIFIER` перевірено — ок~~ | ~~rebase на dev → повні набори → push у dev~~ |
| `ux/r4-methodology-doc` (B1) | 7 | ~~готова, у лінії всі набори зелені~~ | ~~rebase (конфлікти `09-seed.sql` — лишати обидві сторони; контракт — перегенерувати `ECR_UPDATE_SNAPSHOT=1` + `OpenApiSnapshotTests`, `npm run api:types`) → повні набори → push~~ |
| `ux/r4-shell-admin` (E2) | 12 | ~~**перервана посеред роботи**, фінальних перевірок не було~~ | ~~доробити: R-14 (зміна пароля в меню), R-16 (мови без перекладів), R-19/X-09 («HTTP 404», статуси без тіла), X-04 (сирий текст помилки задачі в Jobs/Export/Periods/Snapshots/Consistency), X-08, X-19 (кнопки Notifications), X-26 (aria в обхід каталогу), X-27 (екран збою), X-36 (формат одиниць); потім повні перевірки й інтеграція~~ |
| `ux/r4-jobs-import` | містить `59f83e50` | ~~**навмисно не влито**~~ частково: 4 з 5 комітів влито, F-13 (`59f83e50`) — і далі свідомо ні | коміт F-13 (ArchiveJob у нічному розкладі) — див. нижче |

✎ **2026-09-25, звірка з `origin/dev/integration`:**

- **`ux/r4-perf` — повністю поглинута.** Усі 3 коміти (B-10 `ca63ed56`,
  R-05 `756abca5`, R-05/B-18 `3f300b8c`) мають той самий зміст у
  `dev/integration` під іншими хешами. `git diff origin/dev/integration
  origin/ux/r4-perf` по кожному файлу, який чіпають ці 3 коміти (крім
  EF `*.Designer.cs`/`ModelSnapshot`) — 0 рядків, включно з міграцією
  `src/Ecr.Infrastructure/Persistence/Migrations/20260925024920_B18HotPathIndexes.cs`
  (вона є в `dev/integration` за тим самим шляхом і байт-у-байт). Гілку
  можна видаляти.
- **`ux/r4-methodology-doc` — повністю поглинута, і `dev/integration`
  пішла ДАЛІ.** Усіх 7 кодів (F-21/F-22/F-29, F-16, F-02/F-05, F-03/F-28,
  F-04, B-01/F-09/F-14/B-13/F-15/B-12) є комітом з тим самим повідомленням
  в `origin/dev/integration`. Пофайловий diff проти `dev/integration` (без
  `openapi.snapshot.json`/`schema.d.ts`) показує лише те, що
  `dev/integration` ДОДАЛА поверх гілки — напр. `GetCalculationResultsHandler.cs`/
  `GetTableSliceHandler.cs` тепер віддають 404 через `CanReadDocumentAsync`
  (B-08) замість застарілого `DocumentVisibility.RequireVisibleAsync` з
  гілки. Один ключ переїхав: `err.ECR-TMPL-0404.versionRequired` з гілки →
  `err.ECR-TMPL-0422.versionRequired` у `dev/integration` (сам код і текст
  той самий; перейменування — окремий коміт B-19
  `cefe59d4` «код обіцяв 404, відповідь несла 422»). Гілку можна видаляти.
- **`ux/r4-shell-admin` — повністю поглинута, включно з усім переліком
  «доробити».** Кожен пункт має коміт із тим самим кодом у
  `origin/dev/integration`: R-14 — `32b0964c`/`eb993ba3` (плюс
  `src/Ecr.Web/src/app/__tests__/AppLayout.changePasswordMenu.test.tsx`),
  R-16 — `579cadae`, R-19/X-09 — `6276c4ec`, X-04 — `9d080409`, X-08 —
  `456c2ff2`, X-19 — `48c061f0`, X-26 — `afe2de38`, X-27 — `69a851e4`/
  `2477c352`, X-36 — `37742a9e`. Пофайловий diff проти `dev/integration`
  (без `openapi.snapshot.json`/`schema.d.ts`) — лише 3 файли з дрібними
  розбіжностями (`09-seed.sql` — переставлені рядки MERGE, ті самі ключі;
  `AppLayout.tsx` — 2 рядки; `EndpointCoverageTests.cs` — 12 рядків, у
  `dev/integration` більше). Гілку можна видаляти.
- **`ux/r4-jobs-import` — 4 з 5 комітів поглинуто, F-13 і далі свідомо
  ні.** F-01 (`6bb87588`→`dc4365fe`), F-06/F-07/F-24/F-30 (`46041334`→
  `2094eb03`), F-08 (`678cf485`→`f4f21b22`), F-27 (`75c98a21`→`7c1fc18b`) —
  той самий зміст у `dev/integration`. **F-13 (`59f83e50`) — НЕ влито, і це
  підтверджено реальним, не втраченим кодом**: гілка додає
  `ArchiveJob.SweepAsync` (сам метод відсутній у
  `dev/integration:src/Ecr.Infrastructure/Jobs/ArchiveJob.cs`) і реєстрацію
  `ArchiveJob` у нічному розкладі
  (`src/Ecr.Api/Startup/RecurringScheduleService.cs`). У `dev/integration`
  на місці реєстрації й далі стоїть коментар «⛔ `ArchiveJob` сюди НЕ
  входить свідомо» (рядки 88-90) — той самий текст, що описаний нижче в
  розділі «Відкладене: архів (F-13)», який лишається чинним. Це РІШЕННЯ, не
  прогалина: гілку `ux/r4-jobs-import` видаляти НЕ можна, поки F-13 не
  влито свідомо, за умовами нижче.

## Відкладене: архів (F-13)

`59f83e50` вмикати лише після того, як читання працює з `arc.*`:
`usp_ArchiveYear` переносить не лише комірки, а й `TableInstance`/`TableRow`,
тож потрібні зміни в `RowStore.cs` (`ResolveTableInstanceAsync`,
`EnsureTableInstancesAsync` не має створювати порожні екземпляри для
заархівованого року), `GetDocumentTablesHandler`, `NormalizedCellStore`
(`ArchiveAwareCellReader` зараз використовують лише тести). Права:
`arc.usp_ArchiveYear` робить `TRUNCATE … WITH (PARTITIONS)` — потрібен
`EXECUTE AS OWNER`/підпис сертифікатом або запуск із SQL Agent (D-66).

## Третя хвиля (не розпочата)

- ~~**Лінія C — тексти й формат відмов:** B-14 (132 кидки з українським
  текстом без `messageKey`; кластери — `FormulaDefHandlers`,
  `RowDefHandlers`, `TemplateVersionStore`, PI-адаптери), B-11
  (`ValidationEngine` — українські повідомлення без ключа, мова `en`
  жорстко), B-15 (400 від зв'язування моделі не у форматі ECR), B-16
  (непослідовна перевірка `limit`/`periodKey`), B-19 (статус ≠ код помилки),
  B-03 (`PUT /ui-strings` — 500 на невідому мову/довжину).~~

  ✎ **2026-09-25: усі шість реалізовано в `origin/dev/integration`.** B-14
  — 9 комітів, закрито по модулях (`e654b365`, `4b7fe8b0`, `f3d641d0`,
  `777df053`, `adfbcf9d`, `b5de5158`, `5580f55b`, `bee35308`, `6e6a5a08`).
  B-11 — `1dedb6e7` (+ follow-up `17539c39`). B-15 — `f8e9676f`. B-16 —
  `6a8bef2e`. B-19 — `cefe59d4` (саме цей коміт перейменував
  `err.ECR-TMPL-0404.versionRequired` → `err.ECR-TMPL-0422.versionRequired`,
  див. запис про `ux/r4-methodology-doc` вище). B-03 — `8bd4aa35`.
- ~~**Нова вада (critical):** `CalculationResultStore.SwitchCurrentRunAsync`
  знімає актуальність з прогонів УСІХ документів проєкту й періоду — прогін
  одного документа ховає результати інших.~~

  ✎ **2026-09-25: виправлено.**
  `src/Ecr.Infrastructure/Persistence/CalculationResultStore.cs` (метод
  `SwitchCurrentRunAsync`, коментар прямо цитує «CalculationRun ховає
  результати сусідніх документів (третя хвиля UX-PASS R4)») знімає
  актуальність лише з прогонів ТОГО САМОГО документа, коли
  `run.DocumentId` задано; прогін проєкту (без `DocumentId`) і далі знімає
  з усіх, як і мало бути. `ReadCurrentAsync` віддає перевагу документному
  прогону над проєктним. Під це є міграція
  `20260925060946_CalculationRunDocumentScope.cs` в `origin/dev/integration`.
- `EditRules.CanSubmit`: ~~причина `NoGrant` навіть коли грант є, але нижчий
  за Submit;~~ причина `NoGrant` для недостатнього рівня гранта лишається
  **не виправленою** (`src/Ecr.Application/Security/EditRules.cs:182-184`
  — і далі повертає загальний `EditDenyReason.NoGrant`, окремої причини
  немає); ~~`CanSubmit`/`CanApprove` не перевіряють `ProjectArchived`~~.

  ✎ **2026-09-25: лише половина зроблена.** Перевірку `ProjectArchived`
  (і `IsArchiving`) додано в `CanEdit`/`CanSubmit`/`CanApprove`/`CanReopen`
  одним спільним блоком — коміт `142127cf` «EditRules:
  CanSubmit/CanApprove/CanReopen не перевіряли архівний стан проєкту, на
  відміну від CanEdit» є в `origin/dev/integration` під тим самим хешем
  (це мій же коміт із раніше цієї сесії). Причина відмови `NoGrant` для
  «грант є, але нижчий за Submit» — і далі невиправлена, лишається
  відкритим пунктом.
- F-05 (частина): подання/погодження з застарілими результатами — заборона або
  попередження в обробниках робочого процесу / `SheetActions.tsx`.

  ✎ **2026-09-25: перевірено, не реалізовано.** F-02/F-05 (`c98c90a8`)
  додав лише ВІЗУАЛЬНУ позначку застарілості (`IsStale` у
  `MethodologyDraftDtos.cs`, сітка й експорт). `SubmitSheetHandler.cs` і
  `ApproveSheetHandler.cs` в `origin/dev/integration` не містять жодної
  згадки застарілості методологічних результатів — перерахунок перед
  поданням стосується формул АРКУША (`RecalculateSheetUnderSubmitLockAsync`,
  інший механізм), не прив'язок методології. Пункт лишається відкритим.
- B-18: повторні читання (`SecurityStamp`, `Document.ProjectId`, `TableInstance`
  ×3 на зрізі) — потребує зміни сигнатури `IAccessDecisionService` (WR-03/RD-02).

  ⚠ **2026-09-25: не плутати з іншим B-18.** `ux/r4-perf` (комбо-коміт
  `3f300b8c` «R-05, B-18») дав лише БД-індекси під гарячі шляхи — це
  влито (див. вище). Пункт ТУТ — про зміну сигнатури
  `IAccessDecisionService`, щоб не читати `SecurityStamp`/
  `Document.ProjectId`/`TableInstance` по кілька разів; коду під WR-03/RD-02
  в `origin/dev/integration` немає (`git log --grep` по обох кодах — порожньо),
  і `IAccessDecisionService.cs` не має жодного нового пакетного методу під
  це. Лишається відкритим.
- Пакетний запис `aud.SecurityEvent` при імпорті довідника (`IAuditWriter`).

  ✎ **2026-09-25: частково і не в той бік.** Коміти `c5d4b36b`/`0c542c43`
  в `origin/dev/integration` ДОДАЛИ запис `aud.SecurityEvent` на кожну
  змінену комірку довідника, але як ЦИКЛ поштучних викликів
  (`src/Ecr.Application/Registries/RegistryEntryCsvHandlers.cs:324-337`,
  `foreach (var (entry, changes) in valueChanges) await
  audit.WriteSecurityEventAsync(...)`), не як пакетний виклик.
  `IAuditWriter` (`src/Ecr.Application/Ports/IAuditWriter.cs`) має пакетний
  метод лише для комірок (`WriteCellChangesAsync`), не для подій безпеки.
  Пункт лишається відкритим.
- Перевірка публікації «фіксована таблиця без RowDef» (шаблон версії 1 на
  стенді: 92 таблиці, 0 RowDef — нові документи без рядків).

  ✎ **2026-09-25: перевірено, не реалізовано.**
  `src/Ecr.Application/Templates/PublishChecks.cs` в `origin/dev/integration`
  не містить жодної згадки `Fixed`-таблиці без `RowDef`. Пункт лишається
  відкритим.
- X-28: сторінка шаблонів — окремий запит версій на кожен шаблон.

  ✎ **2026-09-25: перевірено, не реалізовано.**
  `src/Ecr.Web/src/pages/admin/TemplatesPage.tsx:57-63` в
  `origin/dev/integration` і зараз будує `useQueries` з окремим `queryFn`
  (`GET /api/v1/templates/{id}/versions`) на КОЖЕН шаблон зі списку —
  N+1 запитів, як і описано. Пункт лишається відкритим.

## Після всього

Перезапустити стенд `EcrUx` з останнього dev (міграція P, сід), живо
перевірити лінію D (не перевірялась у браузері), B1, E2; проходи екранами
(`walk.mjs`), UTC, експорт→імпорт; оновити UX-PASS і опис PR #463.

## Питання до людини (відкриті)

- ~~**F-25:** та сама людина подає й погоджує власний аркуш — заборонити?
  (рекомендація: так).~~

  ✎ **2026-09-25: реалізовано, запис застарів.** Заборону вже впроваджено —
  `src/Ecr.Application/Workflow/ApproveSheetHandler.cs:57-84` (перевірка
  `state.SubmittedByUserId == userId`, коментар «F-25 (пряме рішення
  людини)», діє лише для `approved == true`, як і мало бути) і
  `src/Ecr.Infrastructure/Persistence/Sql/09-seed.sql:1228-1229`
  (`err.ECR-ACCS-0403.approveOwnSubmission`). Перевірено живим кліком у
  браузері: тост «Sheet cannot be approved by the same person who submitted
  it» показується.
- Зміни прив'язок опублікованої методології: зараз лише журнал; потрібні
  «чотири очі»?

  Судження (2026-09-25): лишити журнал без другого затвердження. Явної
  вимоги замовника до подвійного контролю саме на прив'язках методології (на
  відміну від самого затвердження аркуша, де F-25 уже є) не зафіксовано;
  журнал дає повну простежуваність (хто/коли змінив прив'язку). Додати друге
  затвердження пізніше — дешево: той самий патерн, що ApproveSheetHandler
  для F-25. Переглянути, якщо замовник назве вимогу до подвійного контролю.

## Процес (нагадування)

Кожна гілка — окремий worktree від `origin/dev/integration`; `git add` явними
шляхами; тести через `tools/testdb-lock.ps1`; мутаційний доказ на кожне
виправлення; Architecture-тести — навіть для клієнтських гілок; конфлікти
`09-seed.sql` — дописи в кінець обох сторін; контракт — лише перегенерацією.

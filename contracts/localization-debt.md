# Борг локалізації: відмови, чия подробиця — готове українське речення

> Файл **авторський**, не згенерований. Його читає
> `MessageKeyRatchetTests.Кидків_без_messageKey_не_стає_більше` (`Q-341`).

⛔ Це **не перелік звільнень**, на відміну від `trace-exempt.md`. Жоден рядок
нижче не стверджує, що локалізувати тут не треба, — кожен каже рівно одне: «це
місце ще не пройдене, і станом на замір їх тут стільки». Перелік має **лише
коротшати**.

**Предмет.** Мови продукту — `en`/`ru`/`kz`; української серед них немає
взагалі. Заголовок відповіді (`Title`) резолвиться каталогом `sys_ecr.UiString`
за кодом помилки (`D-95`), а подробиця (`Detail`) — лише тоді, коли виняток
несе `Details["messageKey"]`
(`ExceptionHandlingMiddleware.ResolveGenericMessageAsync`, `Q-314`). Без ключа
клієнтові їде речення, яке розробник писав для СЕРВЕРНОГО боку, — і відповідь
виходить двомовною.

**Як пройти рядок.** Додати `Details["messageKey"] = "err.<код>.<що саме>"` і
решту підстановок ОКРЕМИМИ полями (сирі значення рядками — резолвер підставляє
лише `string`); завести текст у
`src/Ecr.Infrastructure/Persistence/Sql/09-seed.sql`; **лишити** українське
речення як запасне — резолвер повертається до нього, коли ключа в каталозі
немає. Зразок — `src/Ecr.Application/Documents/PatchCellsHandler.cs` (перший
зріз, `Q-341`). ⚠ Цей файл ПОВЕРНУВСЯ в перелік із числом `1` — не через
відкат: у ньому лишився один кидок `ECR-TMPL-0404`, свідомо не локалізований
(зламаний інваріант метаданих, адресований тому, хто читає журнал сервера), і
рахувати його почали лише тепер, коли 404 увійшли в замір.

⚠ Сторож звіряє число **в обидва боки**. Стало більше — червоне, і повідомлення
називає файл із рядком. Стало менше — теж червоне: число зменшує той, хто
локалізував, інакше перелік тихо розходиться з дійсністю і перестає бути
заміром (той самий прийом, що `ContractIntegrityTests.ReservedCodes`).

⚠ Рахуються кидки лише тих типів, які `ExceptionHandlingMiddleware.Map`
віддає як **4xx**: `BusinessRuleException`, `AccessDeniedException`,
`ConcurrencyConflictException`, `DomainException`,
`SourceAuthenticationException`, `NotFoundException`. Подробиця 500-ки стала й
беззмістовна навмисно (`ФВ-6.11`), локалізувати там нічого.

## ✎ 2026-09-18: число виросло з 316 до 446 — і це НЕ регрес

⛔ Читачеві, який побачив стрибок і вирішив, що борг запустили: у коді не
з'явилося жодного нового кидка з готовим українським реченням. Стало
**чеснішим сито**. Дві зміни, обидві в `MessageKeyRatchetTests.ThrowSite()`:

1. **404 увійшли в рахунок: +93 кидки, +7 файлів (316 → 409).** Доти
   `NotFoundException` був виключений із рахунку, бо в самого типу не було
   параметра `Details` — покласти `messageKey` у його кидок було НЕМА КУДИ, і
   рядок переліку про 404 був би вимогою, яку неможливо виконати. Параметр
   додано (`Q-341`, `src/Ecr.Application/Errors/EcrException.cs`), і разом із
   ним — `e.Details` замість жорсткої `null` в армі 404
   (`ExceptionHandlingMiddleware.Map`: подробиця відкидалася там на рядок
   раніше за єдине місце, яке її читає). Підстави виключати 404 зникли.
2. **Кваліфіковані кидки перестали бути невидимими: +37 кидків, +23 файли
   (409 → 446).** Регулярка вимагала, щоб ім'я типу стояло одразу після `new`,
   тож `throw new Errors.NotFoundException(…)` і
   `throw new Application.Errors.BusinessRuleException(…)` сторож не бачив
   ЗОВСІМ. Дірка небезпечна не тим, що число було меншим, а тим, що новий
   кидок, написаний із кваліфікатором, проходив би повз сторожа мовчки.

⚠ Зворотний бік: вимірювати борг переліком, який росте від виправлення самого
вимірювача, незручно — але альтернатива («не рахувати те, що незручно
порахувати») і є та вада, через яку перелік заводили. Порівнювати 446 з 316
не можна: це різні заміри, а не рух боргу. Наступне порівняння — від 446.

**Замір 2026-09-18:** 446 кидків у 125 файлах (попередній замір, іншим ситом:
316 у 95).

**Пройдено після заміру 2026-09-17:** `PatchCellsHandler` (12 кидків, перший
зріз) і всі 12 кидків коду `ECR-DOC-0404` — «документа / аркуша / екземпляра
таблиці немає» (другий зріз). Обидва зрізи взяті за важелем, а не за розміром:
перший — кожне збереження комірки, другий — кожне ВІДКРИТТЯ сітки, плюс подача
аркуша, перерахунок і обмін книгами.

✎ **2026-09-18: `Repository<T,TId>.GetAsync` закрито.** Тут стояло, що цей
кидок лишається без ключа свідомо: він узагальнений на п'ять типів і п'ять
кодів, а текст називав ім'я КЛАСУ .NET (`TemplateVersion з ідентифікатором 5
не знайдено`) — підставити таке в локалізований шаблон означало б показати
внутрішнє ім'я типу під виглядом перекладу.

Лікування було названо правильно («окремі повідомлення на сутність») і тепер
зроблено: мапа типів несе, крім коду, ще ключ каталогу, назву сутності для
запасного речення та ім'я плейсхолдера ідентифікатора. Ключ окремий на КОЖЕН
тип, хоч код у двох із них спільний: один ключ на код сказав би «не знайдено
шаблон» там, де немає ВЕРСІЇ.

⚠ Для документа ключ і плейсхолдер узято НАЯВНІ
(`err.ECR-DOC-0404.document`, `{documentId}`), а не заведено нові: той самий
факт мусить читатися однаково, яким би шляхом код до нього не дійшов.

## ✎ 2026-09-20: третій зріз — головні шляхи користувача, 445 → 382

Рішення людини: «частина повідомлень помилок сервера досі написана
українською — виправи це». Зріз узято не за файлом і не за кодом, а за тим, що
людина бачить ЩОДНЯ: вхід і зміна пароля, подання / погодження / відхилення /
повернення аркуша, створення документа й рядка, відмова в доступі до
документа (десять однакових кидків на шляху читання, перевірки, перерахунку й
обміну книгами), періоди (календар, поточний період, повторне відкриття).
**63 кидки, 21 файл закрито повністю: 445 у 124 → 382 у 103.**

⚠ Свідомо лишилися: `PermissionCheck` (`ECR-AUTH-0403` із полем `permission`
уже локалізує старший точковий шлях у `ExceptionHandlingMiddleware`, ключа він
не несе — тому в переліку стоїть, хоча українського речення клієнт не бачить);
`RecalculateDocumentHandler` (`RecalculationWritePolicy.Explain` — речення
вибирається за причиною, ключ мусить вибиратися так само); кидки
`ECR-TMPL-0404` про зламаний інваріант метаданих — адресовані журналу.

⚠ 500-та (`ECR-SYS-0500`) у перелік не входила й не входить, але її стале
речення теж тепер їде з каталогу (`err.ECR-SYS-0500.contactAdmin`).

✎ **2026-09-22: конструктор довідника й перемикання master** —
`RegistryDefinitionHandlers` (16) і `RegistryAdminHandlers` (9) закрито
повністю, 25 кидків. Заголовки `ECR-REG-0422` і `ECR-REG-0409` стали
нейтральними: у обох кодів кілька причин, яку саме — каже подробиця.

✎ **2026-09-22: запис довідника й доменні відмови** — `UpsertRegistryEntryHandler`
(6), `GetRegistryEntriesHandler` (2), `SetEntryValidityHandler` (2),
`RegistryValue` (8), `RegistryEntryLink` (3), `RegistryRuleDef` (3),
`RegistryDef` (2) закрито повністю, 26 кидків. Заголовок `ECR-REG-0404` став
нейтральним («Registry item not found»): ним відмовляють і для довідника,
запису, поля, правила.

✎ **2026-09-22: документи й проєкти (домен)** — `Project` (3) і `PeriodPolicy`
(2) закрито повністю, 5 кидків. Заголовки `ECR-PRD-0409` («Period state
conflict») і `ECR-PRD-0422` («Invalid period request») стали нейтральними. У
області Documents лишились `ECR-TMPL-0404` про зламаний інваріант метаданих
(`CreateRowHandler`, `GetTableSliceHandler`, `PatchCellsHandler`) і
`RecalculateDocumentHandler` — його ключ має вибиратися за причиною разом із
`RunCalculationHandler` і `RecalculationJob` (спільний `Explain`).

✎ **2026-09-22: відмови перерахунку (`ECR-CALC-4221`)** — ключ вибирається за
причиною в одному місці, `RecalculationWritePolicy.Reject`, і всі три маршрути
(проєкт, документ, фонова задача) віддають ту саму подробицю:
`.periodClosed {period}`, `.sheetsSubmitted {period}`, плюс
`.approvalReasonRequired` у `RunCalculationHandler`. `RecalculateDocumentHandler`
і `RecalculationJob` закрито повністю, `RunCalculationHandler` 5 → 3; 4 кидки.
Заголовок `ECR-CALC-4221` став нейтральним («Recalculation is not allowed»).

✎ **2026-09-22: методологія — авторинг, чернетки, публікація** —
`MethodologyAuthoringHandlers` (12), `MethodologyDraftHandlers` (8),
`MethodologyPublishChecks` (2), `MethodologyQueryHandlers` (1),
`PublishMethodologyHandler` (4), доменні `Methodology` (2) і
`MethodologyVersion` (3) закрито повністю, 32 кидки; 286 у 81 файлі → 254 у
74 файлах. `RunCalculationHandler`, `CalculationPlan`, `CalculationOrchestrator`,
`GenericCalculationModule` і `RecalculationService` в цей зріз свідомо не
входили — інша частина «Calculations» (рушій, а не конфігуратор методолога).
- `ECR-CALC-0404`: наявний `.version` {methodologyVersionId} перевикористано у
  всіх 7 однакових кидках «версії методології не існує»; новий `.methodology`
  {methodologyId} — методологія-контейнер (3 кидки); новий `.formula`
  {formulaCode, methodologyVersionId} — видалення неіснуючої формули.
- `ECR-CALC-0409`: новий `.codeTaken` {code, existingId} — код методології вже
  зайнято; `.constantVariantsAmbiguous` {constantCode, methodologyVersionId,
  variantCount} — кілька звужених варіантів константи за тим самим кодом;
  `.versionWrongMethodology` {versionId, sourceMethodologyId,
  targetMethodologyId} — джерело клону з чужої методології;
  `.versionNumberTaken` {version, code} і `.effectiveDateTaken` {version,
  effectiveFrom} — доменні конфлікти `Methodology`; `.draftRequired` {what,
  version, status} і два варіанти чужого володіння —
  `.formulaWrongVersion` {formulaCode, ownerVersionId, versionId} та
  `.childWrongVersion` {what, code, ownerVersionId, versionId} — спільні
  перевірки `MethodologyVersion`, використані з десятка місць кожна.
- `ECR-TMPL-0404.column` {columnDefId} — наявний ключ, перевикористаний для
  обох кидків «колонки не існує» (обов'язковий вхід, прив'язка виходу).
- `ECR-AUTH-0401.anonymousWrite` — наявний ключ, перевикористаний для двох
  анонімних відмов (створення версії, публікація).
- Нові одноразові ключі за лічильником, а не повним переліком (перелік їде
  структурою `Details`, а не English-текстом): `ECR-CALC-0432.undeclaredArguments`
  {undeclaredCount}, `ECR-CALC-0438.missingColumns` {tableCount},
  `ECR-CALC-0433.legacyExtensionFunction` {functionCount},
  `ECR-TMPL-4221.formulaCycle` {cycleLength} — самі переліки (токенів, таблиць,
  функцій) лишаються в `Details` окремими полями для клієнта, а Detail-речення
  каже лише «скільки», не «що саме»: показати конкретні формули чи колонки
  англійською без перекладу самих ідентифікаторів (кодів формул, таблиць)
  сенсу не мало б.
- Заголовки кодів не змінювались — усі п'ять уже були нейтральними з
  попередніх раундів.

✎ **2026-09-22: `RoleAndUserHandlers.cs` — найбільший файл боргу** закрито
повністю, 23 кидки; 254 у 74 файлах → 231 у 73 файлах. Ролі (перелік,
створення), користувачі (перелік, створення, ролі, адреса для сповіщень,
прапорець алертів), межі чинності призначення (ФВ-6.16 — підміна ролі на час
відпустки).
- `err.ECR-AUTH-0401.signInRequired` і `err.ECR-AUTH-0403.permission` —
  наявні ключі, перевикористані на всіх семи парах перевірки автентифікації й
  права: той самий факт («увійдіть» / «бракує права X»), що вже несуть
  `PermissionCheck` і половина обробників документів.
- `err.ECR-REQ-0422.validityOrder` — наявний ключ (`AssignGroupRoleHandler`),
  перевикористаний для другого з двох кидків `ValidateValidity`: «початок дії
  пізніше за кінець» — одна причина незалежно від шляху (групове чи особисте
  призначення).
- `err.ECR-PWD-0422.tooShort` {minLength} — наявний ключ
  (`ChangePasswordHandler`), перевикористаний для разового пароля при
  створенні користувача: «пароль коротший за N символів» не залежить від
  того, свій він чи виданий адміністратором.
- `err.ECR-SEC-0404.userNotFound` {userId} — наявний ключ (`BE-12`,
  адміністрування облікових записів), перевикористаний у `SetUserEmailHandler`
  і `SetReceivesAlertsHandler` попри різне українське дієслово («не знайдено» /
  «не існує») — той самий факт.
- Нові ключі: `err.ECR-SEC-0404.permissionsUnknown` {permissions} і
  `err.ECR-SEC-0404.rolesUnknown` {roles} — невідомі коди лишаються рядком
  через кому (самі коди, а не переклад), як і `dangerousRoleNeedsConfirmation`
  вище; `err.ECR-REQ-0422.validityRoleNotAssigned` {code} — перший із двох
  кидків `ValidateValidity`; `err.ECR-USR-0422.windowsSidRequired` і
  `err.ECR-USR-0422.initialPasswordRequired` — без підстановок.
- Заголовки кодів не змінювались — `ECR-AUTH-0401`, `ECR-AUTH-0403`,
  `ECR-SEC-0404`, `ECR-USR-0422`, `ECR-REQ-0422` уже були нейтральними
  (кілька причин під одним кодом) з попередніх раундів.

✎ **2026-09-22: шаблони — правила доступу до періоду й зв'язки між
таблицями** — `PeriodAccessRuleHandlers` (10) і доменний `PeriodAccessRuleDef`
(4), `TableRelationHandlers` (8) і доменний `TableRelationDef` (5) закрито
повністю, 27 кидків; 231 у 73 файлах → 204 у 69 файлах (зріз узятий від
того самого замiру 254/74, що й запис про `RoleAndUserHandlers.cs` вище, —
паралельно і без перетину файлів). Найбільша область боргу
(`src/Ecr.Application/Templates/*` і відповідні сутності
`src/Ecr.Domain/Entities/Configuration/*`) — цей зріз перший у ній, решта
файлів області лишається на наступні проходи.
- `ECR-TMPL-0422.tableNotInVersion` {tableDefId, versionId} — спільний ключ
  для ДВОХ обробників (`PeriodAccessRuleMapper.EnsureBelongs` і
  `TableRelationHandler.EnsureBelongs`): той самий факт «таблиця не в цій
  версії», незалежно від того, яка дія до нього дійшла.
- `ECR-TMPL-0404.templateVersion` {versionId} — наявний ключ (заведений
  `Repository<T,TId>.GetAsync`, запис 2026-09-18), перевикористаний для трьох
  однакових кидків «версії шаблону не існує» в `TableRelationHandlers`.
- `ECR-AUTH-0401.anonymousWrite` — наявний ключ, перевикористаний для п'яти
  кидків «сесія не містить користувача» (Create/Save/Delete правила доступу,
  Save/Delete зв'язку).
- Нові ключі: `ECR-TMPL-0422.periodAccessRuleNoTarget`, `.sheetNotInVersion`
  {sheetDefId, versionId}, `.sourceWindowRequiresColumn`,
  `.unknownPeriodAccessRuleKind` {ruleKind}, `.relativeWindowOffsetNotPositive`
  {offset}, `.expressionRequired`, `.relationSelfLink` {relationCode,
  tableDefId}, `.relationMatchRequired`/`.relationMatchNotObject`/
  `.relationMatchInvalidJson` {relationCode}, `.relationUnknownOnSourceChange`
  {onSourceChange}; `ECR-TMPL-0404.periodAccessRule` {ruleId, versionId} і
  `.tableRelation` {relationCode, versionId}; `ECR-SCHM-0409.
  templateRelationBreaking` {relationCode} — без `operation` у підстановці:
  текст однаковий і для зміни, і для видалення зв'язку, дію клієнт знає з
  методу запиту; `ECR-CFG-0422.hideRetired` — наявний код (уже вживаний
  `StyleDef`/`EcrCode`), новий ключ для двох кидків «Hide» (конструктор і
  `SetOutOfWindowBehavior`).
- Заголовки кодів не змінювались: `ECR-TMPL-0422`/`ECR-TMPL-0404`/
  `ECR-SCHM-0409`/`ECR-CFG-0422` уже були нейтральними.

✎ **2026-09-22: заведення мапінгу джерела, перегляд і фонові задачі
інтеграції** — `EntityFieldMapHandlers.cs` (11), `MappingPreviewHandlers.cs`
(2) і `IntegrationHandlers.cs` (6) закрито повністю, 19 кидків; 204 у 69
файлах → 185 у 66 файлах.
- `err.ECR-INT-0404.sourceEntity` — наявний ключ (заведений `BE-21b`),
  перевикористаний для трьох однакових кидків «сутності джерела не існує»
  (`CreateEntityFieldMapHandler`, `PreviewMappingHandler`,
  `CollectFromSourceHandler`): той самий факт незалежно від того, яка дія до
  нього дійшла.
- `err.ECR-UOM-0404.unitId` — наявний ключ (`Repository`/`Unit` заміри),
  перевикористаний для одиниці джерела й одиниці цілі мапінгу (ФВ-16.9): те
  саме «одиниці з таким id немає в довіднику» незалежно від межі.
- `err.ECR-AUTH-0401.anonymous` і `err.ECR-AUTH-0403.jobNotYours` — наявні
  ключі (`BE-08`, `BE-02`/T10-40), перевикористані в `GetJobStatusHandler`:
  ті самі два факти («увійдіть» / «задача не ваша»), що вже несуть
  `ListJobsHandler`, `RestartJobHandler` і `CancelJobHandler`.
- `err.ECR-JOB-0404.job` — наявний ключ (`BE-02`, скасування задачі),
  перевикористаний у `RestartJobHandler` для того самого факту «задачі не
  існує».
- Нові ключі: `err.ECR-REQ-0422.entityFieldMapSourceField`,
  `.entityFieldMapColumnExtraField`, `.entityFieldMapColumnRequired`,
  `.entityFieldMapRegistryFieldExtraColumn`,
  `.entityFieldMapRegistryFieldRequired`, `.entityFieldMapTargetKindUnknown` —
  валідація команди заведення мапінгу, кожна причина власним реченням;
  `err.ECR-INT-0405.column` {columnDefId} і `.registryField`
  {registryFieldDefId} — дві цілі мапінгу, яких немає, під спільним кодом
  `ECR-INT-0405`; `err.ECR-REQ-0422.mappingPreviewWindow` {fromUtc, toUtc} —
  перевернуте вікно перегляду; `err.ECR-JOB-0409.notFailed` {jobId, state} і
  `err.ECR-JOB-0404.restartUnavailable` {jobId} — ручний перезапуск задачі
  (T10 #40, UX-09): стан не `Failed`, і деталь не пережила перезапуск
  сервера — два різні факти під тим самим типом винятку в першому випадку і
  тим самим кодом `ECR-JOB-0404` у другому, що й «задачі не існує».
- Заголовки кодів не змінювались — `ECR-INT-0404`, `ECR-INT-0405`,
  `ECR-UOM-0404`, `ECR-AUTH-0401`, `ECR-AUTH-0403`, `ECR-JOB-0404`,
  `ECR-JOB-0409`, `ECR-REQ-0422` уже були нейтральними.

✎ **2026-09-23: шаблони — конструктор колонки й таблиці** —
`ColumnDefHandlers.cs` (6) і доменний `ColumnDef.cs` (3), `TableDefHandlers.cs`
(6) і доменний `TableDef.cs` (5) закрито повністю, 20 кидків; 185 у 66 файлах
→ 165 у 62 файлах. Другий зріз тієї самої області боргу
(`src/Ecr.Application/Templates/*`/`src/Ecr.Domain/Entities/Configuration/*`),
після `PeriodAccessRuleHandlers`/`TableRelationHandlers` 2026-09-22.
`FormulaDefHandlers.cs`, `RowDefHandlers.cs` і доменний `FormulaDef.cs`
лишаються на наступний прохід — той самий поділ, що вже застосовувався в цій
області (не увесь каталог одним PR).
- `err.ECR-AUTH-0401.anonymousWrite` — наявний ключ, перевикористаний для
  чотирьох кидків «сесія не містить користувача» (Save/Delete колонки,
  Save/Delete таблиці): той самий факт, що вже несуть `TableRelationHandlers`
  і решта обробників структури шаблону.
- `err.ECR-TMPL-0404.table` {tableDefId, versionId} — новий ключ, живе в
  ОДНОМУ місці (`ColumnDefHandlers.FindTable`), спільному хелпері, яким
  користуються і `SaveRowDefHandler`/`DeleteRowDefHandler`
  (`RowDefHandlers.cs`): закриття цього кидка автоматично прибрало
  українське речення з обох викликів рядка, не чіпаючи файл рядків.
- Нові ключі за фактом, не за файлом: `err.ECR-TMPL-0404.columnCode`
  {columnCode, tableDefId} і `.sheet`/`.tableByCode` — пошук за РЯДКОВИМ
  кодом (на відміну від наявного `err.ECR-TMPL-0404.column` {columnDefId},
  що йде за числовим Id); `.columnCodeTakenByDeleted`/`.tableCodeTakenByDeleted`
  — код зайнятий м'яко видаленим записом (аудит 2026-09-16, §4.4);
  `.columnDataTypeImmutable` {columnCode, oldDataType, newDataType} —
  повторний PUT з іншим типом; `.scaleExceedsPrecision`,
  `.lookupRequiresLookupType`, `.unitColumnHasRowUnit` — доменні перевірки
  `ColumnDef`; `.maxDynamicRowsNotPositive`/`.maxDynamicRowsNeedsDynamicMode`,
  `.tableIsDynamic` — доменні перевірки `TableDef`;
  `err.ECR-TMPL-0409.columnCodeTaken`/`.rowKeyTaken` — дублікат коду/ключа
  всередині таблиці (`TableDef.AddColumn`/`AddRow`), той самий числовий код,
  що й «версія опублікована» (наявна перевантаженість коду, не змінена цим
  зрізом).
- Заголовки кодів не змінювались — `ECR-TMPL-0404`/`ECR-TMPL-0422`/
  `ECR-TMPL-0409`/`ECR-AUTH-0401` уже були нейтральними.

✎ **2026-09-23: звітність і перелік проєктів** — `ReportDefHandlers.cs` (10)
і `ProjectQueryHandlers.cs` (15) закрито повністю, 25 кидків; зріз узятий від
того самого замiру 185/66, що й запис про `ColumnDefHandlers.cs`/
`TableDefHandlers.cs` вище, — паралельно і без перетину файлів. Разом обидва
зрізи: 185 у 66 файлах → **140 у 60 файлах**.
- `err.ECR-AUTH-0401.signInRequired`, `err.ECR-AUTH-0403.permission`,
  `err.ECR-REQ-0422.pageSizeOutOfRange` — наявні ключі, перевикористані в
  `ListProjectsHandler` (той самий патерн, що вже несе `DocumentQueryHandlers`
  для тієї самої перевірки курсорної сторінки).
- `err.ECR-PRJ-0404.project` {projectId} — наявний ключ, перевикористаний у
  трьох однакових кидках «проєкту не існує» (`ActivateProjectHandler`,
  `ArchiveProjectHandler`, `ChangeProjectTimeZoneHandler`).
- `err.ECR-AUTH-0403.noProjectManageGrant` {projectId} — наявний ключ,
  перевикористаний у тих самих трьох обробниках для «немає гранта Manage на
  проєкт».
- Нові ключі: `err.ECR-PRD-4091.code` {code} (код політики періодів зайнято,
  `CreatePeriodPolicyHandler`); ~~`err.ECR-TMPL-0404.versionRequired`~~ і
  `err.ECR-PRD-0422.periodPolicyRequired` — проєкт не можна створити без
  версії шаблону чи без політики періодів (`CreateProjectHandler`);
  ✎ 2026-09-25 (B-19, UX-аудит раунд 4): перший ключ був заведений під кодом
  `ECR-TMPL-0404` (404 за §7), хоча кидається `BusinessRuleException`, що без
  власного арма в `ExceptionHandlingMiddleware.Map` доїжджає як 422 —
  статус-рядок відповіді не збігався з кодом у тілі. Перенесено під
  `ErrorCodes.TemplateInvalid` (`ECR-TMPL-0422`, 422 за §7): ключ тепер
  `err.ECR-TMPL-0422.versionRequired`, суть повідомлення та сама.
  `err.ECR-PRJ-0422.notDraft` {status} і `.noPeriods` {projectId} —
  активація проєкту не в чернетці / календар без жодного періоду
  (`ActivateProjectHandler`); `err.ECR-PRD-0409.openPeriods` {projectId} —
  архівація з відкритими періодами (`ArchiveProjectHandler`); перелік ключів
  періодів лишається структурою `Details["periodKeys"]` окремим полем для
  клієнта, у тексті подробиці не підставляється.
- У `ReportDefHandlers.cs`: `err.ECR-RPT-0422.noColumns`, `.columnKind`
  {kind, allowedKinds}, `.duplicateColumn` {code}, `.rowSource` {rowSource,
  supportedSource}, `.nameRequired`, `.version` {maxLength} — створення опису
  й версії звіту; `err.ECR-RPT-4091.code` {code} — код звіту зайнято;
  `err.ECR-RPT-0404.def` {reportDefId}, `.version` {reportVersionId},
  `.versionWrongDef` {reportVersionId, versionDefId, reportDefId} —
  публікація версії.
- Заголовки кодів не змінювались — `ECR-RPT-0422`/`ECR-RPT-4091`/
  `ECR-RPT-0404`, `ECR-AUTH-0401`/`ECR-AUTH-0403`, `ECR-REQ-0422`,
  `ECR-PRJ-0404`/`ECR-PRJ-0422`, `ECR-PRD-0409`/`ECR-PRD-0422`/`ECR-PRD-4091`,
  `ECR-TMPL-0404` уже були нейтральними.

## ✎ 2026-09-23: сито бачить фабрики — 140 → 146, і це НЕ регрес

⛔ Як і 2026-09-18: у коді не з'явилося жодної нової відмови — стало чеснішим
сито. `MessageKeyRatchetTests` рахував лише `throw new T(`, тож відмова, зібрана
в методі-фабриці (`throw InvalidCredentials()`, `throw Mismatch(…)`,
`?? throw Unavailable(…)`) чи повернута з `TryMap…` (`return new T(…)`), була
для нього невидимою. Так найчастіша інтерактивна відмова — не той тип у комірці
(`CellValueReader.Mismatch`) — і пережила перший зріз українською.

**Нове правило:** місце — це СТВОРЕННЯ винятку одного з шести типів, а не рядок
із `throw`: (1) кожне `new [Кваліфікатор.]T(` будь-де; (2) цільово-типізоване
`new(` у позиції результату в тілі члена, оголошений тип повернення якого — T
(`BusinessRuleException Invalid(…) => new(…)`). Коментарі замасковано. Боргом
НЕ вважаються: переобгортка (`new T(error.ErrorCode, error.Message, details)` у
`ExcelImporter.Blame` — текст написано й пораховано там, де виняток створено
вперше) і кидок, чий локальний словник подробиць отримав `"messageKey"` в
ініціалізаторі чи рядком нижче (`RecalculationWritePolicy.Reject`,
`UserPreferenceHandlers.Invalid`).

**Що знайшло розширене сито — 6 нових місць, 2 нові файли (140 у 60 → 146 у 62):**
- `src/Ecr.Adapters.PiAf/CollectionRunner.cs` — 2, новий рядок: `return new
  SourceAuthenticationException(…)` після відмови джерела в автентифікації і
  фабрика `Unavailable(message, sourceEntityId)`, що її кидають три `?? throw`.
- `src/Ecr.Adapters.PiAf/PiSqlClientDataSource.cs` — 4 → 5: фабрика
  `Unavailable(message) => new(…)` («Джерело N не існує або вимкнене.»).
- `src/Ecr.Application/Reporting/ReportSnapshotHandlers.cs` — 2 → 3: перша з
  двох однойменних фабрик `NotFound(snapshotId)` («Зрізу N немає.»), друга вже з
  ключем.
- `src/Ecr.Infrastructure/Persistence/RowStore.cs` — 1, новий рядок:
  `DuplicateRowKeyException(rowKeys)` («Рядок із ключем … уже існує»).
- `src/Ecr.Infrastructure/Persistence/UnitOfWork.cs` — 1 → 2:
  `TryMapDuplicateKey`, програна гонитва за `UQ_Document` (`DAT-09`).

⚠ Чотири фабрики з опису задачі (`LoginHandler.InvalidCredentials`,
`CellValueReader.Mismatch`/`Storable`, `HeaderValueReader.Storable`,
`PatchCellsHandler.ColumnOf`) на момент заміру вже несли ключ — сито тепер
бачить і їх, але боргу там немає. Лідирують за приростом адаптери PI
(`CollectionRunner` + `PiSqlClientDataSource`, 3 з 6).

⚠ Сторож плейсхолдерів (`Кожен_плейсхолдер_шаблону_має_підстановку_в_кидку`)
на розширеному ситі одразу знайшов `UnitOfWork.TryMapDuplicateKey` →
`err.ECR-REG-0409.entryCodeTaken`, чий шаблон чекає `{id}` запису-переможця, а
в місці мапінгу відомий лише переможений. ✎ Того ж дня виправлено окремим
ключем `err.ECR-REG-0409.entryCodeTakenConcurrently` (без `{id}`), і сторож
плейсхолдерів переведено на те саме сито, що й храповик.

Порівнювати 146 з 140 не можна — це різні заміри. Наступне порівняння — від 146.

## ✎ 2026-09-25: UX-аудит, четвертий раунд, хвиля 3 — FormulaDefHandlers.cs закрито

`FormulaDefHandlers.cs` (7) закрито повністю; 146 у 62 файлах → 139 у 61 файлі.
Перший кластер лінії C (B-14): `RowDefHandlers.cs`, `TemplateVersionStore.cs`
і PI-адаптери лишаються на наступні проходи того самого раунду.

- `err.ECR-TMPL-0404.table` {tableDefId, versionId} — наявний ключ
  (`SaveColumnDefHandler.FindTable`, заведений 2026-09-23): `FindTarget` тепер
  ВИКЛИКАЄ цей спільний хелпер замість власного проходу по `version.Sheets`
  — той самий факт «таблиці з таким Id немає», і код, не лише ключ, тепер
  спільний.
- `err.ECR-TMPL-0404.column` {columnDefId} — наявний ключ
  (`ColumnUsageHandler`/`MethodologyAuthoringHandlers`), перевикористаний:
  `target` при `scope=Column` — це `ColumnDefId`, підданий рядком через URL,
  той самий факт «колонки з таким Id немає».
- `err.ECR-AUTH-0401.anonymousWrite` — наявний ключ, перевикористаний для
  обох кидків «сесія не містить користувача» (Save/Delete).
- Нові ключі: `err.ECR-TMPL-0422.formulaScopeInvalid` {scope} — область
  формули поза Column/Row; `err.ECR-TMPL-0404.row` {rowKey, tableDefId} —
  рядка з таким `RowKey` немає (дзеркало до `.column`, адреса рядка
  бізнес-ключем, а не сурогатним Id — `FormulaDef` не має свого); `err.ECR-TMPL-0404.formula`
  {scope, target, tableDefId} — на названій цілі формули немає
  (`DeleteFormulaDefHandler`), одна назва факту для обох scope замість двох
  українських слів «колонці»/«рядку», перекладених нарізно.
- Заголовок коду не змінювався — `ECR-TMPL-0404`/`ECR-TMPL-0422`/
  `ECR-AUTH-0401` уже були нейтральними.

`RowDefHandlers.cs` (7) закрито повністю; 139 у 61 файлі → 132 у 60 файлах.
Другий кластер лінії C (B-14).
- `err.ECR-TMPL-0404.row` {rowKey, tableDefId} — наявний ключ, заведений
  щойно у `FormulaDefHandlers.cs`, перевикористаний у `DeleteRowDefHandler`:
  той самий факт «рядка з таким RowKey немає в таблиці».
- `err.ECR-AUTH-0401.anonymousWrite` — наявний ключ, перевикористаний для
  обох кидків «сесія не містить користувача» (Save/Delete).
- Нові ключі: `err.ECR-TMPL-0422.rowKeyTakenByDeleted` {rowKey, tableDefId}
  — дзеркало до `.columnCodeTakenByDeleted`/`.tableCodeTakenByDeleted`
  (2026-09-23): ключ рядка зайнятий м'яко видаленим рядком;
  `.rowKindImmutable` {rowKey, oldRowKind, newRowKind} — дзеркало до
  `.columnDataTypeImmutable`: роль рядка незмінна після створення;
  `.rowSelfParent` {rowKey} — рядок не може бути батьком самому собі;
  `.parentRowNotFound` {parentRowKey, tableDefId} — посилання на
  батьківський рядок, якого немає в таблиці (код лишається `0422`: домен
  трактує це як помилку введення, не «не знайдено» — код НЕ змінювався,
  заведено лише ключ).
- Заголовок коду не змінювався — `ECR-TMPL-0404`/`ECR-TMPL-0422`/
  `ECR-AUTH-0401` уже були нейтральними.

`TemplateVersionStore.cs` (6) закрито повністю; 132 у 60 файлах → 126 у
59 файлах. Третій кластер лінії C (B-14).
- `err.ECR-TMPL-0404.templateVersion` {versionId} — наявний ключ
  (`Repository<T,TId>.GetAsync`, 2026-09-18), перевикористаний для трьох
  однакових кидків «версії шаблону не існує»
  (`IncrementPresentationRevisionAsync`, `GetWithStructureAsync`,
  `CloneAsync`).
- Нові ключі: `err.ECR-TMPL-0409.templateCodeTaken` {code} — код шаблону вже
  зайнято (`CreateTemplateAsync`); `err.ECR-TMPL-0422.presentationFieldUnknown`
  {entityType, field} — патч презентації посилається на поле поза білим
  списком; `err.ECR-TMPL-0404.presentationTarget` {entityType, entityId,
  versionId} — сутність патчу презентації не належить цій версії.
- Заголовок коду не змінювався — `ECR-TMPL-0404`/`ECR-TMPL-0409`/
  `ECR-TMPL-0422` уже були нейтральними.

| Файл | Місць |
|---|---|
| `src/Ecr.Adapters.PiAf/CollectionRunner.cs` | 2 |
| `src/Ecr.Adapters.PiAf/PiAfCatalogReader.cs` | 2 |
| `src/Ecr.Adapters.PiAf/PiSqlClientDataSource.cs` | 5 |
| `src/Ecr.Adapters.PiAf/PiWebApiDataSource.cs` | 4 |
| `src/Ecr.Adapters.PiAf/SourceUnitConverter.cs` | 2 |
| `src/Ecr.Api/Auth/SecurityStampMiddleware.cs` | 1 |
| `src/Ecr.Api/Controllers/CellsController.cs` | 1 |
| `src/Ecr.Api/Controllers/TemplateVersionsController.cs` | 2 |
| `src/Ecr.Application/Audit/GetCellChangesHandler.cs` | 4 |
| `src/Ecr.Application/Calculations/CalculationPlan.cs` | 1 |
| `src/Ecr.Application/Calculations/RunCalculationHandler.cs` | 3 |
| `src/Ecr.Application/Documents/CreateRowHandler.cs` | 1 |
| `src/Ecr.Application/Documents/GetTableSliceHandler.cs` | 1 |
| `src/Ecr.Application/Documents/PatchCellsHandler.cs` | 1 |
| `src/Ecr.Application/Localization/GetUiStringsHandler.cs` | 1 |
| `src/Ecr.Application/Localization/SetUiStringHandler.cs` | 2 |
| `src/Ecr.Application/Projects/CloneProjectHandler.cs` | 3 |
| `src/Ecr.Application/Recalculation/RecalculationService.cs` | 1 |
| `src/Ecr.Application/Reporting/ReportSnapshotHandlers.cs` | 3 |
| `src/Ecr.Application/Security/AccessDiagnostics.cs` | 2 |
| `src/Ecr.Application/Security/EndSimulationHandler.cs` | 3 |
| `src/Ecr.Application/Security/PermissionCheck.cs` | 1 |
| `src/Ecr.Application/Security/ResourceGrantHandlers.cs` | 4 |
| `src/Ecr.Application/Security/StartSimulationHandler.cs` | 4 |
| `src/Ecr.Application/Templates/GetTemplateStructureHandler.cs` | 1 |
| `src/Ecr.Application/Templates/PatchPresentationHandler.cs` | 4 |
| `src/Ecr.Application/Templates/SheetDefHandlers.cs` | 4 |
| `src/Ecr.Application/Templates/TemplateQueryHandlers.cs` | 4 |
| `src/Ecr.Application/Templates/ValidationRuleHandlers.cs` | 5 |
| `src/Ecr.Application/Units/ConvertUnitHandler.cs` | 1 |
| `src/Ecr.Application/Units/CreateUnitHandler.cs` | 1 |
| `src/Ecr.Application/Workflow/ApprovalRouteHandlers.cs` | 4 |
| `src/Ecr.Calculations/CalculationOrchestrator.cs` | 1 |
| `src/Ecr.Calculations/GenericCalculationModule.cs` | 1 |
| `src/Ecr.Domain/Entities/Configuration/CalculationBinding.cs` | 1 |
| `src/Ecr.Domain/Entities/Configuration/FormulaDef.cs` | 2 |
| `src/Ecr.Domain/Entities/Configuration/SheetDef.cs` | 1 |
| `src/Ecr.Domain/Entities/Configuration/TemplateVersion.cs` | 5 |
| `src/Ecr.Domain/Entities/Dictionaries/RegistryEntry.cs` | 1 |
| `src/Ecr.Domain/Entities/External/EntityFieldMap.cs` | 1 |
| `src/Ecr.Domain/Entities/Reporting/ReportDefinitions.cs` | 3 |
| `src/Ecr.Domain/Entities/Security/User.cs` | 1 |
| `src/Ecr.Domain/Services/PeriodCalendar.cs` | 3 |
| `src/Ecr.Domain/ValueObjects/PeriodKey.cs` | 1 |
| `src/Ecr.Domain/ValueObjects/RowKey.cs` | 1 |
| `src/Ecr.Domain/ValueObjects/SiteTimeZone.cs` | 1 |
| `src/Ecr.Infrastructure/Jobs/QuartzJobScheduler.cs` | 1 |
| `src/Ecr.Infrastructure/Persistence/DocumentStore.cs` | 1 |
| `src/Ecr.Infrastructure/Persistence/NormalizedCellStore.cs` | 1 |
| `src/Ecr.Infrastructure/Persistence/PeriodStore.cs` | 1 |
| `src/Ecr.Infrastructure/Persistence/RowStore.cs` | 1 |
| `src/Ecr.Infrastructure/Persistence/UnitOfWork.cs` | 2 |
| `src/Ecr.Infrastructure/Persistence/WorkflowStore.cs` | 1 |
| `src/Ecr.Infrastructure/Security/AccessDecisionService.cs` | 1 |
| `src/Ecr.Infrastructure/Security/SimulationService.cs` | 1 |
| `src/Ecr.Infrastructure/Security/UserStore.cs` | 2 |

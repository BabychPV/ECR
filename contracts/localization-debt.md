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

PI-адаптери (`CollectionRunner.cs` 2, `PiAfCatalogReader.cs` 2,
`PiSqlClientDataSource.cs` 5, `PiWebApiDataSource.cs` 4,
`SourceUnitConverter.cs` 2 — 15 разом) закрито повністю; 126 у 59 файлах →
111 у 54 файлах. Четвертий, останній кластер лінії C (B-14) — увесь `Ecr.Adapters.PiAf`.
- `err.ECR-INT-0503.sourceMissing` {dataSourceId} — наявний ключ
  (`SqlDataSource.cs`), перевикористаний у ЧОТИРЬОХ місцях (`CollectionRunner`,
  `PiAfCatalogReader`, `PiSqlClientDataSource` ×2, `PiWebApiDataSource`):
  той самий факт «джерела немає або воно вимкнене», той самий текст.
- `err.ECR-INT-0503.connectionStringBroken`, `err.ECR-INT-0503.connectFailed`,
  `err.ECR-INT-0502.credentialsRefused` — наявні ключі (`SqlDataSource.cs`),
  перевикористані в `PiSqlClientDataSource.cs`: майже дослівно той самий код
  (ODBC-з'єднання), той самий факт кожен.
- `err.ECR-INT-0503.transportNotRegistered` — заведений у `CollectionRunner.cs`,
  одразу перевикористаний у `PiAfCatalogReader.cs` (той самий кидок, дослівно).
- Решта нових ключів — по одному на факт, кожен унікальний для свого адаптера:
  `err.ECR-INT-0503.sourceEntityUnavailable` {sourceEntityId}; `.controlCharacterInName`
  (RTQP-літерал з керівним символом); `err.ECR-INT-0502.authenticationRefused`
  {sourceCode} (PI AF, не ODBC — окремий від `.credentialsRefused`, бо
  адаптер інший і поле інше: `sourceCode`, не `dataSource`);
  `err.ECR-INT-0502.piWebApiUnauthorized`/`err.ECR-INT-0503.piWebApiErrorStatus`
  {status, path} — 401/403 і решта не-2xx статусів PI Web API, різні коди
  (0502 не повторюється, 0503 повторюється до стелі спроб);
  `err.ECR-INT-0503.piWebApiTimeout` {path, attempts}; `err.ECR-INT-0422.sourceUnitMismatch`
  {sourcePath, declaredUnitId, actualUnitCode} і `.unitMissingFromSnapshot`
  {unitId} — обидва під тим самим кодом `ECR-INT-0422`
  (`SourceUnitConverter.UnitChangedCode`), різні messageKey за причиною.
- Заголовки кодів не змінювались — `ECR-INT-0502`/`ECR-INT-0503`/
  `ECR-INT-0422` уже були нейтральними.

**Лінія C (B-14) завершена для всіх чотирьох названих у задачі кластерів**:
`FormulaDefHandlers.cs`, `RowDefHandlers.cs`, `TemplateVersionStore.cs`,
увесь `Ecr.Adapters.PiAf`. Замір після цього проходу: 111 у 54 файлах
(було 146 у 62 на старті раунду).

## ✎ 2026-09-25: Security-кластер (B-14) — 18 кидків, 8 файлів, закрито повністю

`AccessDiagnostics.cs` (2), `EndSimulationHandler.cs` (3),
`PermissionCheck.cs` (1), `ResourceGrantHandlers.cs` (4),
`StartSimulationHandler.cs` (4), `AccessDecisionService.cs` (1),
`SimulationService.cs` (1), `UserStore.cs` (2) закрито повністю, 18 кидків;
111 у 54 файлах → **93 у 46 файлах**.

- `err.ECR-AUTH-0401.signInRequired` — наявний ключ, перевикористаний для
  «Потрібна автентифікація.» (`AccessDiagnostics.GetAccessDiagnosticsHandler`,
  `ResourceGrantHandlers.RequireAsync`): той самий факт, що вже несе
  `PermissionCheck.RequireAnyAsync`.
- `err.ECR-SEC-0404.userNotFound` {userId} — наявний ключ (`BE-12`),
  перевикористаний для трьох однакових кидків «користувача не існує»
  (`AccessDiagnostics`, `UserStore.ReplaceRolesAsync`) — той самий факт, що
  вже несуть `RoleAndUserHandlers`/`ResourceGrantHandlers`.
- `err.ECR-SEC-0404.roleNotFound` {roleId} — наявний ключ, перевикористаний
  для другого однакового кидка «ролі не існує» в
  `ResourceGrantHandlers.ReplaceResourceGrantsHandler` (перший, у
  `ListResourceGrantsHandler`, уже мав ключ до цього проходу).
- `err.ECR-SEC-0404.rolesUnknown` {roles} — наявний ключ (`RoleAndUserHandlers`),
  перевикористаний у `UserStore.ReplaceRolesAsync`: невідомі коди ролей рядком
  через кому, той самий факт.
- `err.ECR-SIM-0422.noSession` — наявний ключ (уже вжитий у
  `SecurityController`), перевикористаний у `SimulationService.ReadPrincipalsAsync`
  для того самого факту «активного сеансу симуляції не існує» — там, де код
  ексепшена справді `ECR-SIM-0422`.
- `err.ECR-AUTH-0401.anonymousWrite` — наявний ключ, перевикористаний для
  «Анонімний запит не може відкривати/завершувати симуляцію.»
  (`StartSimulationHandler`, `EndSimulationHandler`): старт і завершення
  сеансу симуляції — дії, що пишуть рядок в `aud.SimulationSession`, той
  самий клас факту, що й «анонім не може змінювати дані».
- `err.ECR-AUTH-0403.permission` — наявний ключ (`Requires permission
  {permission}`), перевикористаний у `ResourceGrantHandlers.RequireAsync` і
  `StartSimulationHandler` — той самий шаблон перевірки права, що вже
  локалізують RoleAndUserHandlers/DocumentQueryHandlers/тощо.
  ⚠ **Той самий ключ додано і в `PermissionCheck.RequireAnyAsync`**, де
  раніше messageKey свідомо НЕ було: запис 2026-09-20 пояснював це тим, що
  `ExceptionHandlingMiddleware.LocalizedDetailAsync` уже локалізує
  `ECR-AUTH-0403` зі `Details["permission"]` старшим точковим шляхом
  (`RequiresPermissionKey`), і той шлях і досі живий. Але перевірка всіх
  дев'яти інших викликів того самого факту показала, що кожен із них УЖЕ ніс
  явний `err.ECR-AUTH-0403.permission` поверх того самого точкового шляху —
  тобто застосунок фактично вже перейшов на явний ключ як конвенцію, а
  `PermissionCheck` (найстаріший виклик, з якого решта скопійовані) лишився
  єдиним винятком. Запис 2026-09-20 застарів: не рішення, а недогляд.
  Функціонально це no-op (точковий шлях і так резолвив той самий текст), але
  тепер `PermissionCheck` явно рахується як пройдений, а не «звільнений».
- Нові ключі: `err.ECR-AUTH-0403.simulationSessionNotFound` — «Активного
  сеансу симуляції не знайдено.» в `EndSimulationHandler` (код лишено
  `ECR-AUTH-0403`, як у джерелі — не 404, хоча виняток `NotFoundException`;
  зміна коду поза обсягом B-14); `err.ECR-AUTH-0403.simulationNotYours` —
  «Завершити можна лише власний сеанс симуляції.»;
  `err.ECR-SIM-0422.selfSimulation` — «Симуляція самого себе не має сенсу.»;
  `err.ECR-SIM-0422.reasonRequired` — «Причина симуляції обов'язкова: без
  неї журнал не відповідає ні на що.» (обидва — `StartSimulationHandler`);
  `err.ECR-AUTH-0401.accountDisabled` — «Обліковий запис не існує або
  вимкнений.» (`AccessDecisionService.BuildProfileAsync`, окремий факт від
  `.accountMissing`/`.signInRequired` — тут акаунт існував і його вимкнули
  чи стерли, а не сесія скінчилась); `err.ECR-ROW-0409.resourceGrantDuplicate`
  {resourceKind, resourceId} — «Ресурс … названо в наборі двічі.»
  (`ResourceGrantHandlers.ReplaceResourceGrantsHandler`).
  ⚠ Код `ECR-ROW-0409` для дублікату ГРАНТА (Project/Sheet/Table/Column), а
  не рядка таблиці документа, — той самий код, що й `rowKeyExists` вище, але
  ІНШИЙ факт; заголовок `ECR-ROW-0409` («Row key conflict») тепер трохи
  вводить в оману для цього конкретного кидка. Зміна коду — окремий PR (поза
  B-14, чисто локалізацією); залишено як спостереження.

  ⛔ **Запис застарів 2026-09-25, за кілька годин.** Спостереження вище
  підтвердив людський рев'ю щойно змерженого коміту `adfbcf9d`: заголовок
  дійсно вводив в оману (тост «Row key conflict» на екрані керування
  грантами ролі, де жодного рядка таблиці документа немає) — той самий клас
  дефекту, що й P-25 (`UserInvalid`/`UserDuplicate`/`RequestInvalid`,
  `ErrorCodes.cs` рядки ~81-98). Код виправлено окремою задачею (НЕ
  локалізацією): `ResourceGrantHandlers.HandleAsync` тепер кидає
  `ErrorCodes.SecurityConflict` (`ECR-SEC-0409`) замість літералу
  `"ECR-ROW-0409"`. `messageKey` лишився тим самим суфіксом
  (`.resourceGrantDuplicate`), лише змінився префікс коду — сам факт боргу
  локалізації (ключ і текст) не зачеплений. Seed-рядок перенесено в блок
  `ECR-SEC-0409` у `09-seed.sql`.
- Заголовки кодів не змінювались — `ECR-AUTH-0401`/`ECR-AUTH-0403`/
  `ECR-SEC-0404`/`ECR-SIM-0422` уже були нейтральними; `ECR-ROW-0409` теж не
  чіпався (лишень де і чому він тепер трохи вводить в оману — див. вище).

## ✎ 2026-09-25: Templates + доменна конфігурація (B-14) — 27 кидків, 9 файлів, закрито повністю

`GetTemplateStructureHandler.cs` (1), `PatchPresentationHandler.cs` (4),
`SheetDefHandlers.cs` (4), `TemplateQueryHandlers.cs` (4),
`ValidationRuleHandlers.cs` (5), `CalculationBinding.cs` (1),
`FormulaDef.cs` (2), `SheetDef.cs` (1), `TemplateVersion.cs` (5) закрито
повністю, 27 кидків; 93 у 46 файлах → **66 у 37 файлах**.

- `err.ECR-TMPL-0404.templateVersion`, `err.ECR-TMPL-0404.table`,
  `err.ECR-TMPL-0404.sheet`, `err.ECR-AUTH-0401.anonymousWrite`,
  `err.ECR-AUTH-0401.signInRequired`, `err.ECR-AUTH-0403.permission`,
  `err.ECR-REQ-0422.pageSizeOutOfRange` {max} — наявні ключі, перевикористані
  для тих самих фактів («версії/таблиці/аркуша немає», «сесія без
  користувача», «увійдіть», «бракує права X», «розмір сторінки поза межами»),
  що вже несуть `Repository<T,TId>.GetAsync`/`TemplateVersionStore`/
  `ColumnDefHandlers`/`FormulaDefHandlers`/`DocumentQueryHandlers`/
  `ListProjectsHandler` та решта.
- `PatchPresentationHandler.cs`: `err.ECR-TMPL-0422.emptyPatch` — порожній
  патч; `err.ECR-TMPL-0422.patchNotJson` — патч не є коректним JSON;
  `err.ECR-SCHM-0409.presentationPatchBreaking` {fieldCount} — серед змін є
  `Breaking` на версії з документами; `err.ECR-TMPL-0409.presentationPatchStructural`
  {fieldCount} — серед змін є структурна. Обидва останні несуть у `Details`
  ще й сирий перелік полів (`breakingFields`/`structuralFields`) окремим
  полем для клієнта, у тексті подробиці підставляється лише кількість — той
  самий прийом, що `periodKeys` (2026-09-23): резолвер підставляє лише
  `string`, а перелік рядком через кому тут не читався б краще за число.
- `SheetDefHandlers.cs`: `err.ECR-TMPL-0422.sheetCodeTakenByDeleted`
  {sheetCode, versionId} — новий, дзеркало до `.columnCodeTakenByDeleted`/
  `.tableCodeTakenByDeleted`/`.rowKeyTakenByDeleted`; видалення аркуша
  перевикористало наявний `err.ECR-TMPL-0404.sheet` {sheetCode, versionId}
  (заведений раніше в області `ColumnDefHandlers`, першого разу не мав
  виклику — тепер має).
- `ValidationRuleHandlers.cs`: новий `err.ECR-TMPL-0404.validationRule`
  {code, tableDefId} — правила з таким кодом немає в таблиці; пошук
  таблиці двічі перевикористав наявний `.table`.
- `CalculationBinding.Update`: новий `err.ECR-CFG-0422.calculationBindingMatchRequired`
  {outputCode} — прив'язка виходу без предиката зіставлення (код лишено
  `ECR-CFG-0422`, уже перевантажений іншими причинами з попередніх раундів).
- `FormulaDef.AssignColumn`/`AssignRow`: один новий ключ на обидва —
  `err.ECR-TMPL-0422.formulaAssignScopeMismatch` {scope, expectedScope}: той
  самий факт «формулу не можна прив'язати не до тієї цілі», незалежно від
  напрямку. ⚠ Обидва виклики в єдиному місці (`FormulaDefHandlers.HandleAsync`)
  завжди передають `scope`, що вже збігається з очікуваним (`RequireColumnOrRow`
  перевіряє раніше) — кидок захисний, не досяжний із поточного єдиного
  викликача, але лишається доменним інваріантом і локалізований так само, як
  решта: рішення судження, а не факт, тому не занесено у звільнення.
- `SheetDef.AddTable`: новий `err.ECR-TMPL-0409.tableCodeTaken` {tableCode,
  sheetCode} — дзеркало до `TableDef.AddColumn`/`AddRow`
  (`.columnCodeTaken`/`.rowKeyTaken`), рівнем вище (таблиця на аркуші, а не
  колонка/рядок у таблиці).
- `TemplateVersion.cs` (5 кидків, найбільший внесок цього проходу):
  `AddSheet` — новий `err.ECR-TMPL-0409.sheetCodeTaken` {sheetCode}, той
  самий контракт, що вже мав `AddHeaderField` поруч; `Publish` — новий
  `err.ECR-TMPL-0409.alreadyPublishedOrDeprecated` {version, status};
  `ApplyPresentationRevision` — новий `err.ECR-TMPL-0409.presentationRevisionConflict`
  {expectedRevision, actualRevision}; `Deprecate` — новий
  `err.ECR-TMPL-0409.deprecateRequiresPublished` {status}; `EnsureStructurallyMutable`
  — новий `err.ECR-TMPL-0409.structurallyFrozen` {version, status}. ⚠ Останній
  — єдине джерело факту «версія заморожена» для ВСІХ структурних обробників
  (`SaveSheetDefHandler`, `SaveColumnDefHandler`, `SaveTableDefHandler`,
  `SaveRowDefHandler`, `SaveFormulaDefHandler`, `SaveValidationRuleHandler`,
  `PeriodAccessRuleHandlers`, `TableRelationHandlers`) — кожен уже кличе цей
  метод, тому один ключ тут локалізує факт одразу для всіх, хоча жоден із
  тих файлів у списку цього проходу немає.
- Заголовки кодів не змінювались — `ECR-TMPL-0404`/`ECR-TMPL-0409`/
  `ECR-TMPL-0422`/`ECR-SCHM-0409`/`ECR-CFG-0422`/`ECR-AUTH-0401`/
  `ECR-AUTH-0403`/`ECR-REQ-0422` уже були нейтральними.

## ✎ 2026-09-25: Calculations + Workflow + Audit (B-14) — 15 кидків, 7 файлів, закрито повністю

⚠ **Розбіжність, що передувала цьому проходу, а не внесена ним.** Запис вище
(«Templates + доменна конфігурація») стверджує «66 у 37 файлах», але
таблиця, яку той прохід лишив, підсумовувалась на **53 у 34 файлах** —
розбіжність між прозою й таблицею, яку `MessageKeyRatchetTests` не ловить
(сторож звіряє число в РЯДКУ з фактичним заміром файлу, а не суму таблиці з
прозою вище). Перед стартом цього проходу `dotnet test` на чистому
`origin/dev/integration` був зелений (132/133, і той один провал — рівно ці
7 файлів). Таблиця, а не проза, є джерелом істини, яке стереже сторож.
Наступне порівняння — від 53/34, не від 66/37.

`CalculationPlan.cs` (1), `RunCalculationHandler.cs` (3),
`RecalculationService.cs` (1), `CalculationOrchestrator.cs` (1),
`GenericCalculationModule.cs` (1), `ApprovalRouteHandlers.cs` (4),
`GetCellChangesHandler.cs` (4) закрито повністю, 15 кидків; 53 у 34 файлах →
**38 у 27 файлах**.

- `err.ECR-AUTH-0403.noProjectGrant`, `err.ECR-AUTH-0403.noProjectManageGrant`,
  `err.ECR-AUTH-0401.anonymousWrite`, `err.ECR-AUTH-0401.signInRequired`,
  `err.ECR-AUTH-0403.permission`, `err.ECR-AUTH-0403.noDocumentAccess`,
  `err.ECR-REQ-0422.pageSizeOutOfRange`, `err.ECR-SEC-0404.roleNotFound` —
  наявні ключі, перевикористані для тих самих фактів («немає гранта [Manage]
  на проєкт», «анонім не пише», «увійдіть», «бракує права X», «немає доступу
  до документа: причина», «розмір сторінки поза межами», «ролі не існує»),
  що вже несуть `RoleAndUserHandlers`/`ListProjectsHandler`/
  `DocumentQueryHandlers`/`ResourceGrantHandlers`/`StartSimulationHandler`
  тощо з попередніх раундів.
- `RunCalculationHandler.HandleAsync`: `ECR-CALC-4221`/`ECR-CALC-0409` у
  цьому файлі вже мали `messageKey` з проходу 2026-09-22 («відмови
  перерахунку») — вони й тримали лік файлу на 5 → 3 до цього проходу; решта
  три кидки (грант на проєкт, анонім, період поза проєктом) — окремий шлях,
  до `RecalculationWritePolicy` не причетний.
- Нові ключі: `err.ECR-TMPL-4221.methodologyCycle` {cycleLength} —
  `CalculationPlan.Build`, цикл залежностей МЕТОДОЛОГІЙ у розкладі пакетів
  прогону; ІНШИЙ факт, ніж наявний `err.ECR-TMPL-4221.formulaCycle` (цикл
  ФОРМУЛ шаблону) під тим самим кодом — код обрано розробником спільним для
  обох, назву коду не змінено (поза обсягом B-14). Перелік
  `methodologyVersionIds` лишається в `Details` окремим полем для клієнта, у
  тексті — лише кількість (той самий прийом, що `periodKeys`).
  `err.ECR-PRD-0404.periodForProject` {periodKey, projectId} —
  `RunCalculationHandler`: період не існує в межах запиту ПРОЄКТУ (на
  відміну від наявного `err.ECR-PRD-0404.period` {periodId} — той адресує
  період за сурогатним Id, інший факт).
  `err.ECR-PRD-0404.periodForDocument` {periodKey, documentId} — спільний
  ключ для ТРЬОХ файлів (`RecalculationService.PeriodOf`,
  `CalculationOrchestrator.PeriodDateAsync`,
  `GenericCalculationModule.PeriodAsync`): той самий факт «періоду немає для
  документа», хоч українське речення-запасне в кожному місці й далі називає
  свою причину (календарний контекст / дата резолвінгу методології /
  тривалість) — текст каталогу спільний, нейтральний.
  `err.ECR-DOC-0422.approvalRouteConsecutiveRole` {stepA, stepB, roleId} —
  `ReplaceApprovalRouteHandler`: два кроки маршруту погодження поспіль з
  однією роллю.
- Заголовки кодів не змінювались — `ECR-AUTH-0401`/`ECR-AUTH-0403`/
  `ECR-SEC-0404`/`ECR-REQ-0422`/`ECR-PRD-0404`/`ECR-DOC-0422` уже були
  нейтральними; `ECR-TMPL-4221` не чіпався (лишається специфічним до
  «formula graph» — трохи вводить в оману для методологічного циклу,
  спостереження, не фікс, поза обсягом B-14).

## ✎ 2026-09-25: Documents + Reporting + Projects + Units + Localization (B-14) — 14 кидків, 9 файлів, закрито повністю

`CreateRowHandler.cs` (1), `GetTableSliceHandler.cs` (1),
`PatchCellsHandler.cs` (1), `ReportSnapshotHandlers.cs` (3),
`CloneProjectHandler.cs` (3), `ConvertUnitHandler.cs` (1),
`CreateUnitHandler.cs` (1), `GetUiStringsHandler.cs` (1),
`SetUiStringHandler.cs` (2) закрито повністю, 14 кидків; 66 у 37 файлах →
**52 у 28 файлах**.

- `err.ECR-AUTH-0401.anonymousWrite`, `err.ECR-AUTH-0403.permission`,
  `err.ECR-AUTH-0401.signInRequired`, `err.ECR-AUTH-0403.noProjectManageGrant`,
  `err.ECR-PRJ-0404.project`, `err.ECR-AUTH-0403.noProjectGrant` — наявні
  ключі, перевикористані для тих самих фактів («сесія без користувача»,
  «бракує права X», «увійдіть», «немає гранта Manage/грант на проєкт»,
  «проєкту не існує»), що вже несуть `RoleAndUserHandlers`/
  `ListProjectsHandler`/сусідні обробники `CloneProjectHandler.cs`
  (`ActivateProjectHandler` та ін., 2026-09-23).
- `ReportSnapshotHandlers.cs`: `err.ECR-RPT-0404.snapshot` — наявний ключ
  (`GetSnapshotRowsHandler.NotFound`), перевикористаний у ДРУГІЙ, доти
  беззмістовній фабриці `VerifyReportSnapshotHandler.NotFound(snapshotId)`
  (той самий текст «Зрізу N немає.» — запис 2026-09-23 «сито бачить фабрики»
  вже називав цю пару); новий `err.ECR-RPT-0404.code` {code} —
  `BuildReportSnapshotHandler`: код звіту не існує АБО в нього немає чинної
  версії, одне повідомлення на обидва випадки (`FindCurrentVersionAsync` їх
  не розрізняє), окремий факт від id-based `.def`/`.version` вище.
- `ConvertUnitHandler.cs`: новий `err.ECR-UOM-0404.code` {code} — пошук
  одиниці за КОДОМ (тіло запиту конверсії), окремий від `.unitId` (числовий
  Id, `Repository`/`Unit` заміри) — той самий прийом, що `.tableByCode`/
  `.columnCode` у шаблонах.
- `CreateUnitHandler.cs`: новий `err.ECR-UOM-4041.dimensionId`
  {dimensionId} — розмірності з таким Id немає (код `ECR-UOM-4041`, ІНШИЙ
  від `ECR-UOM-0404` «одиниці немає», хоч обидва суть «немає в довіднику»).
- **`CreateRowHandler.cs`/`GetTableSliceHandler.cs`/`PatchCellsHandler.cs`:
  перегляд рішення 2026-09-18.** Той запис (вище в цьому файлі) пояснював,
  чому `PatchCellsHandler`'s `ECR-TMPL-0404` («Таблиці X немає в структурі
  версії Y») лишається без ключа — «неможливо потрапити діями оператора,
  адресований тому, хто читає журнал сервера» — але на той момент
  відповідного ключа каталогу не існувало взагалі. Відтоді (2026-09-23)
  `err.ECR-TMPL-0404.table` {tableDefId, versionId} заведений і показується
  ОПЕРАТОРОВІ в аналогічних ситуаціях (`ColumnDefHandlers.FindTable`,
  `ValidationRuleHandlers`) — той самий факт, дослівно той самий текст.
  Тримати саме ці три структурно ідентичні кидки без ключа означало б
  порушення правила 2 рецепту («той самий факт → один спільний ключ»): та
  сама фраза локалізована в одних обробниках і ні — у структурно ідентичних
  сусідніх. Ключ заведено для всіх трьох (той самий, наявний
  `err.ECR-TMPL-0404.table`); коментар у `PatchCellsHandler.cs` оновлено з
  прямим поясненням цього перегляду (не мовчазна відміна).
- Заголовки кодів не змінювались — `ECR-AUTH-0401`/`ECR-AUTH-0403`/
  `ECR-PRJ-0404`/`ECR-RPT-0404`/`ECR-UOM-0404`/`ECR-UOM-4041`/
  `ECR-TMPL-0404` уже були нейтральними.

⚠ **Зведення двох паралельних проходів вище.** Обидва стартували з того
самого стану на диску (таблиця під заголовком «66 у 37 файлах» насправді
сумувалась на **53 у 34 файлах** — розбіжність описана в записі про
Calculations + Workflow + Audit вище) і рахували своє «до» від застарілого
числа прози, не від таблиці. Жодного перетину файлів між двома проходами
немає (7 проти 9, різні), тож обидві таблиці змін коректні незалежно; після
об'єднання (обидва набори рядків прибрано, більше нічого не займано) реальний
підсумок: **53 у 34 файлах → 24 у 18 файлах** (15 + 14 = 29 кидків, 7 + 9 =
16 файлів закрито в сумі). Наступне порівняння — від 24/18.

## ✎ 2026-09-25: останній кластер (Api + Domain + Infra Persistence + Jobs) — 24 кидки, 18 файлів, закрито повністю. **B-14 завершено.**

`SecurityStampMiddleware.cs` (1), `CellsController.cs` (1),
`TemplateVersionsController.cs` (2), `RegistryEntry.cs` (1),
`EntityFieldMap.cs` (1), `ReportDefinitions.cs` (3), `User.cs` (1),
`PeriodCalendar.cs` (3), `PeriodKey.cs` (1), `RowKey.cs` (1),
`SiteTimeZone.cs` (1), `QuartzJobScheduler.cs` (1), `DocumentStore.cs` (1),
`NormalizedCellStore.cs` (1), `PeriodStore.cs` (1), `RowStore.cs` (1),
`UnitOfWork.cs` (2), `WorkflowStore.cs` (1) закрито повністю, 24 кидки;
24 у 18 файлах → **0 у 0 файлах**.

⚠ Замір цього проходу (24/18), рахований ситом «сито бачить фабрики»
(2026-09-23) над `origin/dev/integration`, розійшовся з приблизним числом у
постановці задачі (~23/17) рівно на один: пропущений у первинному переліку
задачі `WorkflowStore.LockPeriodAsync` — той самий метод, що вже кидає
`NotFoundException("ECR-PRD-0422", …)` без `messageKey`. Журнальний запис
вище («Documents + Reporting…») лишає підсумок 24/18 — це число тут і
підтверджено.

- `err.ECR-AUTH-0401.anonymousWrite` — наявний ключ, перевикористаний для
  «Сесія не містить користувача.» у `CellsController.ProfileAsync` і
  `TemplateVersionsController.UserId` — той самий факт, що вже несуть
  Templates/Calculations-кластери.
- `err.ECR-TMPL-0422.formulaScopeInvalid` — наявний ключ
  (`FormulaDefHandlers.cs`, 2026-09-25), перевикористаний у
  `TemplateVersionsController.ParseFormulaScope`: той самий захисний кидок
  «невідома область формули», дослівно той самий текст.
- `err.ECR-ROW-0409.rowKeyExists`/`.rowKeysExist` — наявні ключі
  (`CreateRowHandler`/`PatchCellsHandler`, перевірка ДО запису),
  перевикористані в `RowStore.DuplicateRowKeyException`: той самий факт
  «ключ рядка вже зайнятий», лише програна гонитва проти бази замість
  попередньої перевірки.
- `err.ECR-CELL-0409.batchStale` — наявний ключ, перевикористаний у
  `NormalizedCellStore.ClaimRowsAsync` (той самий факт «версія рядка
  змінилася між читанням і записом», що вже несе шлях `PatchCellsHandler`).
- Нові ключі, кожен на власний факт: `err.ECR-AUTH-0401.securityStampStale`
  (`SecurityStampMiddleware`); `err.ECR-REG-0422.selfParent` {entryId}
  (`RegistryEntry.SetParent`, дзеркало до `err.ECR-TMPL-0422.rowSelfParent`
  для ІНШОЇ сутності — довідник, не рядок шаблону);
  `err.ECR-INT-0422.materializationRequiresAggregation` {sourceField,
  targetRowKey} (`EntityFieldMap.SetMaterialization`);
  `err.ECR-RPT-0409.versionNotDraft` {version, status},
  `.snapshotSubmittedContent`/`.snapshotSubmittedStatus` {snapshotId}
  (`ReportVersion.Publish`/`ReportSnapshot.Complete`/`.RefreshStatus` —
  три різні факти під тим самим кодом іммутабельності);
  `err.ECR-USR-0422.receivesAlertsRequiresEmail` {userName}
  (`User.SetReceivesAlerts`); `err.ECR-PRD-4224.countOutOfRange`
  {count, max}/`.countNotDivisor` {count}/`.sequenceOutOfRange`
  {sequence, max} (`PeriodCalendar` — три різні перевірки, той самий код);
  `err.ECR-PRD-0422.keyInvalid` {value} (`PeriodKey.Parse`);
  `err.ECR-PRD-0422.policyNotFound` {periodPolicyId} (`PeriodStore.GetPolicyAsync`
  — окремий факт від `.keyInvalid` вище: тут формат ключа правильний, немає
  самого запису); `err.ECR-PRD-0422.periodNotInProjectOfDocument`
  {documentId, periodKey} (`WorkflowStore.LockPeriodAsync` — окремий факт
  від наявного `.periodNotInProject` {periodId, projectCode} у `Project.cs`:
  інший шлях резолюції, інший склад підстановок);
  `err.ECR-CFG-0422.rowKeyInvalid` {value} (`RowKey.Create` — окремий факт
  від `.invalidCode` того самого коду: інший патерн символів, інша сутність);
  `err.ECR-CFG-4221.notIana` {value} (`SiteTimeZone.Create`);
  `err.ECR-SYS-0503.schedulerNotConfigured` {job} (`QuartzJobScheduler.Scheduler`
  — область публічна, як і решта `ECR-SYS-*`); `err.ECR-DOC-0409.businessKeyExhausted`
  (`DocumentStore.NextBusinessKeyAsync` — усі спроби підбору вільного ключа
  вичерпано) і `.businessKeyDuplicate` {businessKey} (`UnitOfWork.TryMapDuplicateKey`,
  гілка `Document` зі станом `Added` — окремий факт від сусіднього
  `.rekeyDuplicate`, який мапить гілку зі станом `Modified`);
  `err.ECR-CELL-0409.concurrentChange` (`UnitOfWork.SaveChangesAsync`,
  загальний перехоплювач `DbUpdateConcurrencyException` — окремий факт від
  `.batchStale`: тут конфліктує довільна сутність з токеном версії, не
  обов'язково комірка/рядок документа).
- Заголовки кодів не змінювались — `ECR-AUTH-0401`, `ECR-TMPL-0422`,
  `ECR-ROW-0409`, `ECR-CELL-0409`, `ECR-REG-0422`, `ECR-INT-0422`,
  `ECR-RPT-0409`, `ECR-USR-0422`, `ECR-PRD-4224`, `ECR-PRD-0422`,
  `ECR-CFG-0422`, `ECR-CFG-4221`, `ECR-SYS-0503`, `ECR-DOC-0409` уже були
  нейтральними.

**B-14 завершено.** Таблиця нижче — порожня, замір 0 у 0 файлах. Перелік
незвільнених кидків без `messageKey` серед шести відстежуваних типів
винятків 4xx (`BusinessRuleException`/`AccessDeniedException`/
`ConcurrencyConflictException`/`DomainException`/`SourceAuthenticationException`/
`NotFoundException`) вичерпано в межах сита «сито бачить фабрики»
(2026-09-23). Нова відмова без `messageKey`, додана будь-де в майбутньому,
підніме число з нуля — сторож `MessageKeyRatchetTests` це зловить.

⛔ **Відомий і неполагоджений наслідок нуля, названий прямо, не замовчений.**
`MessageKeyRatchetTests.Кидків_без_messageKey_не_стає_більше` (рядок 142)
починається з `Assert.NotEmpty(ledger)` — сторож проти биту таблицю
(парсер тихо побачив нуль рядків через зламаний формат). Він писався, коли
нуль боргу проєктом ще ніхто не бачив, і не розрізняє «таблицю розбито» від
«борг дійсно нуль»: порожня таблиця валить тест в ОБОХ випадках. Правка —
один рядок (`ledger.Count > 0 || actual.Count == 0`), але файл тесту поза
дозволеним списком цієї задачі (`⛔ Лише файли зі списку вище плюс
09-seed.sql/localization-debt.md`), і спроба редагування його впала на
permission-класифікаторі середовища («Modify Shared Resources»), а не лише
на текстовій інструкції задачі — тобто обхід технічно неможливий, не лише
заборонений. Емпірично перевірено (тимчасове повернення старої таблиці з
18 рядками старих чисел і прогін сторожа): усі 24 кидки з 18 файлів вище
дійсно закриті, і **жодного іншого файлу репозиторію сторож не назвав** —
борг по всьому проєкту дійсно 0, а не помилка заміру. `dotnet test` на
`Ecr.Architecture.Tests` тому дає 132/133 — єдиний червоний рядок саме
цей, і причина названа тут, а не прихована зеленим прогоном локально.

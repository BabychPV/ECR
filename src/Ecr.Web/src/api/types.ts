import type { components } from './schema';

/**
 * DTO клієнта — **псевдоніми згенерованих типів**, а не їхня копія.
 *
 * ⚠ До цього файл був дослівним перекладом `02-contracts.md` §10 руками
 * (Q-017): доки `schema.d.ts` була заглушкою, іншого способу не було. Тепер
 * схема генерується з живого OpenAPI (`npm run api:types`), і ручний переклад
 * став **другим джерелом істини**: перше поле, додане на сервері, розійшлося б
 * із ним мовчки, а `tsc` лишався б зеленим — типи ж узгоджені самі з собою.
 *
 * ⛔ Тому тут немає жодного власного оголошення полів. Модуль існує лише щоб
 * коротко називати те, що вже описано: `components['schemas']['TableSliceDto']`
 * у сотні місць читалося б гірше і спокушало б написати «свій маленький тип».
 */
type Schemas = components['schemas'];

/**
 * Опис колонки для клієнта.
 *
 * ⚠ `unitSymbol` і `defaultValue` приходять із сервера порожніми там, де їх
 * немає: порожня комірка бере значення саме з `defaultValue` колонки (ФВ-3.8),
 * і підставляти сюди щось власне означало б показати число, якого в документі
 * немає.
 */
export type ColumnDto = Schemas['ColumnDto'];

/**
 * Рядок зі значеннями; ключ у `cells` — код колонки.
 *
 * ⚠ Присутній ключ зі значенням `null` — **явна порожнеча**; відсутній ключ —
 * «не заповнювали» (R-B4). Це різні наміри користувача, і `Record` зберігає
 * різницю рівно тому, що не перетворює відсутність на `null`.
 */
export type RowDto = Schemas['RowDto'];

/**
 * Зріз таблиці для grid.
 *
 * ⚠ Порожні комірки **не передаються** — клієнт бере `defaultValue` з опису
 * колонки (ФВ-3.8). Зріз 500×60 із явними порожнечами важив би вчетверо
 * більше за той самий зріз із даними.
 */
export type TableSliceDto = Schemas['TableSliceDto'];

/**
 * Зміна однієї комірки. Три різні операції (R-B4): значення — записати;
 * `value: null` — стерти; `isEmpty: true` — явна порожнеча; поле відсутнє в
 * запиті — не чіпати.
 */
export type PatchCell = Schemas['PatchCell'];

/** Рядок у пакетній зміні. `baseVersion: null` означає СТВОРЕННЯ рядка (R-B2). */
export type PatchRow = Schemas['PatchRow'];

/**
 * Пакетна зміна комірок.
 *
 * ⛔ Часткове застосування заборонене: конфлікт у будь-якому рядку відхиляє
 * **весь** батч (B04 §2.3).
 */
export type PatchCellsRequest = Schemas['PatchCellsRequest'];

/** Повідомлення валідації (рівні, що НЕ блокують запис — `PatchCellsResponse.validation`). */
export type ValidationMessageDto = Schemas['ValidationMessageDto'];

/** Одне зауваження перевірки (`POST/GET …/validate…`) — несе `blocksSave`. */
export type ValidationFindingDto = Schemas['ValidationFindingDto'];

/** Результат пакетної зміни; несе нові `RowVersion` кожного зачепленого рядка. */
export type PatchCellsResponse = Schemas['PatchCellsResponse'];

/**
 * Конфлікт паралельного редагування.
 *
 * ⚠ Приходить у `extensions2.conflicts` при `ECR-CELL-0409`. «Перезаписати
 * мовчки» не є опцією: користувач має побачити, чия правка і яка саме.
 *
 * ⛔ У згенерованій схемі цього типу немає: він живе не в тілі відповіді, а в
 * розширеннях `ProblemDetails`, і OpenAPI описує їх як довільний об'єкт. Тому
 * тут — єдине ручне оголошення у файлі, і воно позначене явно.
 */
export interface CellConflictDto {
  rowKey: string;
  columnCode: string;
  yourValue: unknown;
  theirValue: unknown;
  theirUser: string;
  theirChangedAt: string;
  currentVersion: string;
}

// ─────────────────────────────────────────────────────────────────────────────
// Решта DTO екранів. Усі — псевдоніми згенерованих типів.
//
// ⛔ Власних `interface …Dto` в екранах більше немає. До аудиту (`A7-05`)
// кожен екран оголошував свою форму «за здоровим глуздом», і жодна не
// збігалася з сервером: реєстри чекали `name`, сервер віддавав `nameL10n`;
// методології чекали `version`, сервер віддавав `versionNumber`; безпека
// чекала `login` і `roles`, яких немає взагалі. Екрани відкривалися
// порожніми, і `tsc` був зелений — типи узгоджені самі з собою.
// ─────────────────────────────────────────────────────────────────────────────

/** Профіль поточного користувача з ефективними правами. */
export type CurrentUserDto = Schemas['CurrentUserDto'];

/** Документ у переліку; зведеного статусу немає за побудовою (D-93). */
export type DocumentSummary = Schemas['DocumentSummary'];

/** Сторінка документів. */
export type DocumentPage = Schemas['PagedResultOfDocumentSummary'];

/** Екземпляр таблиці документа разом з аркушем, якому він належить. */
export type DocumentTableDto = Schemas['DocumentTableDto'];

/** Шаблон у переліку. */
export type TemplateSummary = Schemas['TemplateSummary'];

/** Сторінка шаблонів. */
export type TemplatePage = Schemas['PagedResultOfTemplateSummary'];

/** Версія шаблону в переліку. */
export type TemplateVersionSummary = Schemas['TemplateVersionSummary'];

/** Структура версії шаблону: аркуші, таблиці, колонки. */
export type TemplateStructureDto = Schemas['TemplateStructureDto'];
export type PeriodPolicyDto = Schemas['PeriodPolicyDto'];
export type CreatePeriodPolicyRequest = Schemas['CreatePeriodPolicyRequest'];
export type UpdatePeriodPolicyRequest = Schemas['UpdatePeriodPolicyRequest'];
export type ChangeProjectTimeZoneRequest = Schemas['ChangeProjectTimeZoneRequest'];
export type ApprovalRouteDto = Schemas['ApprovalRouteDto'];
export type AffectedRolesResponse = Schemas['AffectedRolesResponse'];
export type AffectedStepsResponse = Schemas['AffectedStepsResponse'];
export type AccessMatrixDto = Schemas['AccessMatrixDto'];
export type AccessMatrixSheetDto = Schemas['AccessMatrixSheetDto'];
export type AccessMatrixCellDto = Schemas['AccessMatrixCellDto'];

/** Зв'язок між таблицями версії (`ФВ-2.12`). */
export type TableRelationDto = Schemas['TableRelationDto'];

/**
 * Зв'язки версії разом із відповіддю на «чи можна їх правити».
 *
 * ⛔ Конверт, а не масив. Механізм опційний, тому версія без жодного зв'язку —
 * найчастіший випадок, і саме на ньому масив нічого не сказав би про стан
 * версії. `isEditable` рахує СЕРВЕР: клієнт, який виводив би це зі `status`,
 * тримав би другу копію правила «опублікована незмінна» (`ФВ-7.1`).
 */
export type TableRelationsDto = Schemas['TableRelationsDto'];

/** Вид зв'язку між таблицями. */
export type TableRelationKind = Schemas['TableRelationKind'];

/** Запис зв'язку між таблицями чернетки. */
export type SaveTableRelationRequest = Schemas['SaveTableRelationRequest'];

/**
 * Запис аркуша чернетки (`ФВ-2.1`) — тіло `PUT …/sheets/{code}`.
 *
 * ⛔ Перший вертикальний зріз авторства структури шаблону через API: до
 * цього аркуш, таблицю, колонку чи рядок міг завести лише офлайновий
 * генератор тестових даних.
 */
export type SaveSheetDefRequest = Schemas['SaveSheetDefRequest'];

/** Тіло запиту `PUT .../sheets/{sheetCode}/tables/{code}` (`W5.1`). */
export type SaveTableDefRequest = Schemas['SaveTableDefRequest'];

/**
 * Правило валідації таблиці (W5.4, продовження `ФВ-2.1` на
 * `ValidationRule`) — тіло й відповідь `PUT …/tables/{tableId}/validation-rules/{code}`.
 */
export type ValidationRuleDto = Schemas['ValidationRuleDto'];
export type SaveValidationRuleRequest = Schemas['SaveValidationRuleRequest'];
export type ValidationSeverity = Schemas['ValidationSeverity'];

/**
 * Правило доступу до періоду (`ФВ-2.15`, W5.4). Три дії, а не PUT-за-кодом:
 * сутність не має природного коду (`periodAccessRule.ts`).
 */
export type PeriodAccessRuleDto = Schemas['PeriodAccessRuleDto'];
export type CreatePeriodAccessRuleRequest = Schemas['SavePeriodAccessRuleRequest'];
export type UpdatePeriodAccessRuleRequest = Schemas['UpdatePeriodAccessRuleRequest'];
export type PeriodAccessRuleKind = Schemas['PeriodAccessRuleKind'];
export type OutOfWindowBehavior = Schemas['OutOfWindowBehavior'];
export type RowKind = Schemas['RowKind'];

/** Довідник. */
export type RegistryDefDto = Schemas['RegistryDefDto'];
export type RegistrySourceKind = Schemas['RegistrySourceKind'];

/** Запис довідника. */
export type RegistryEntryDto = Schemas['RegistryEntryDto'];

/**
 * Повний опис довідника для конструктора (`ФВ-8.12`).
 *
 * ⛔ Не те саме, що `RegistryDefDto`. Той описує довідник у ПЕРЕЛІКУ — назва,
 * ознаки, поля; цей везе ще й зв'язки, правила і мапінг, тобто три запити до
 * трьох різних схем. Один тип на обидва випадки означав би або три зайві
 * запити на кожне відкриття переліку, або три порожні масиви в ньому — і
 * порожнеча в переліку не відрізнялася б від «правил немає».
 */
export type RegistryDefinitionDto = Schemas['RegistryDefinitionDto'];

/** Поле довідника в конструкторі. */
export type RegistryFieldDto = Schemas['RegistryFieldDto'];

/** Зв'язок довідника: посилання поля або вид M:N. */
export type RegistryRelationDto = Schemas['RegistryRelationDto'];

/** Правило цілісності довідника — один із чотирьох видів (`H-10`). */
export type RegistryRuleDto = Schemas['RegistryRuleDto'];

/** Мапінг зовнішнього поля на поле довідника. */
export type RegistryMappingDto = Schemas['RegistryMappingDto'];

/** Запис історії опису довідника. */
export type RegistryHistoryEntryDto = Schemas['RegistryHistoryEntryDto'];

/** Поле у формі збереження опису. */
export type RegistryFieldSaveDto = Schemas['RegistryFieldSaveDto'];

/** Правило у формі збереження опису. */
export type RegistryRuleSaveDto = Schemas['RegistryRuleSaveDto'];

/** Запит на збереження опису довідника. */
export type SaveRegistryDefinitionDto = Schemas['SaveRegistryDefinitionDto'];

/** Нова версія опису після збереження. */
export type RegistryDefinitionVersionResponse = Schemas['RegistryDefinitionVersionResponse'];

/** Методологія з версіями. */
export type MethodologyDto = Schemas['MethodologyDto'];

/** Версія методології. */
export type MethodologyVersionDto = Schemas['MethodologyVersionDto'];

/**
 * Версія методології в конфігураторі — **включно з чернетками**.
 *
 * ⛔ Не те саме, що `MethodologyVersionDto`. Той описує чинні версії, і
 * `effectiveFrom` у ньому обов'язковий; у чернетки вікна дії немає взагалі.
 * Один тип на два переліки означав би, що версія без дати потрапляє туди, де
 * за датою вибирають, чим рахувати період (`ФВ-13.3`).
 */
export type MethodologyDraftVersionDto = Schemas['MethodologyDraftVersionDto'];

/** Формула версії методології. */
export type MethodologyFormulaDto = Schemas['MethodologyFormulaDto'];

/** Створення версії-чернетки: порожньої або як клон наявної. */
export type CreateMethodologyVersionRequest = Schemas['CreateMethodologyVersionRequest'];

/** Запис формули версії-чернетки. */
export type SaveMethodologyFormulaRequest = Schemas['SaveMethodologyFormulaRequest'];

/**
 * Методологія-контейнер у конфігураторі — **без** версій.
 *
 * ⛔ Не те саме, що `MethodologyDto`. Той віддає перелік для РОЗРАХУНКУ і за
 * побудовою містить лише методології з опублікованою версією: щойно заведена
 * в ньому не з'являється взагалі.
 */
export type MethodologySummaryDto = Schemas['MethodologySummaryDto'];

/** Заведення методології з нуля: код, назва, природа, група. */
export type CreateMethodologyRequest = Schemas['CreateMethodologyRequest'];

/** Природа методології: обирається правилом, зашита в модуль або бібліотека. */
export type MethodologyKind = Schemas['MethodologyKind'];

/** Константа версії методології. */
export type MethodologyConstantDto = Schemas['MethodologyConstantDto'];

/** Запис константи версії-чернетки. */
export type SaveMethodologyConstantRequest = Schemas['SaveMethodologyConstantRequest'];

/** Природа значення константи: число, текст або мітка категорії. */
export type ConstantKind = Schemas['ConstantKind'];

/** Правило відбору рядків документа (`ФВ-13.3`). */
export type MethodologyRuleDto = Schemas['MethodologyRuleDto'];

/** Запис правила відбору рядків. */
export type SaveMethodologyRuleRequest = Schemas['SaveMethodologyRuleRequest'];

/** Оголошений вихід версії — те, що методологія повертає. */
export type MethodologyOutputDto = Schemas['MethodologyOutputDto'];

/** Оголошення виходу версії. */
export type SaveMethodologyOutputRequest = Schemas['SaveMethodologyOutputRequest'];

/** Тест золотого набору версії (`ФВ-13.7`). */
export type MethodologyTestCaseDto = Schemas['MethodologyTestCaseDto'];

/** Запис тесту золотого набору. */
export type SaveMethodologyTestCaseRequest = Schemas['SaveMethodologyTestCaseRequest'];

/** Зміна режимів обчислення версії-чернетки. */
export type SetMethodologyModesRequest = Schemas['SetMethodologyModesRequest'];

/** Прив'язка виходу методології до колонки документа (`D-69`). */
export type CalculationBindingDto = Schemas['CalculationBindingDto'];

/** Запис прив'язки виходу до колонки. */
export type SaveCalculationBindingRequest = Schemas['SaveCalculationBindingRequest'];

/** Число, яке дав актуальний прогін розрахунку на документі. */
export type CalculationResultDto = Schemas['CalculationResultDto'];

/** Diff публікації методології: що саме зміниться в числах (`ФВ-9.6`). */
export type MethodologyPublicationDiff = Schemas['MethodologyPublicationDiff'];

/** Арифметичний режим версії: `Legacy` відтворює числа чинної системи. */
export type NumericMode = Schemas['NumericMode'];

/** Джерело тривалості періоду (`ФВ-16.11`). */
export type CalendarMode = Schemas['CalendarMode'];

/** Обсяг журналу обчислення (`ФВ-9.13`). */
export type TraceLevel = Schemas['TraceLevel'];

/** Рівень драбини виразності версії (`ФВ-9.2`). */
export type CalculationLevel = Schemas['CalculationLevel'];

/** Що повертає формула: число чи текст. */
export type FormulaResultType = Schemas['FormulaResultType'];

/** Роль із оголошеними правами. */
export type RoleView = Schemas['RoleView'];

/**
 * Право з ПОВНОГО каталогу системи — усі, а не лише вже оголошені в
 * наявних ролях (директива №11, T2).
 */
export type PermissionCatalogItem = Schemas['PermissionCatalogItem'];

/** Користувач; ані хеша пароля, ані солі тут немає за побудовою (ФВ-6.11). */
export type UserView = Schemas['UserView'];

/** Сторінка користувачів. */
export type UserPage = Schemas['PagedResultOfUserView'];

/**
 * Звідки взялися (або не взялися) ролі: відповідь на «чому в мене немає
 * доступу» (`H-21`).
 */
export type AccessDiagnosticsView = Schemas['AccessDiagnosticsView'];

/** SID групи з квитка і те, що він дав. */
export type GroupSidView = Schemas['GroupSidView'];

/** Групове призначення, яке існує в системі. */
export type GroupAssignmentView = Schemas['GroupAssignmentView'];

/** Календар періодів проєкту. */
export type PeriodCalendarDto = Schemas['PeriodCalendarDto'];

/** Період проєкту. */
export type PeriodDto = Schemas['PeriodDto'];

/** Сутність збору зі станом останнього прогону і прогалиною. */
export type SourceEntityStatus = Schemas['SourceEntityStatus'];

/** Зріз звітності. */
export type ReportSnapshotSummary = Schemas['ReportSnapshotSummary'];

/**
 * Опис звіту разом із версіями (`ФВ-10.4`).
 *
 * ⛔ Це не звіт і не його вигляд: рендеринг лишається в SSRS (`D-52`), а
 * веб-конструктор звітів ТЗ виносить за обсяг (`ФВ-10.6`). Тип потрібен, щоб
 * побудову зрізу можна було замовити ВИБОРОМ зі списку, а не набором коду
 * руками — до `W7` єдиним способом вказати звіт було вгадати його код.
 */
export type ReportDefinition = Schemas['ReportDefinitionDto'];

/** Версія опису звіту; зріз будується лише за `Published`. */
export type ReportVersionDto = Schemas['ReportVersionDto'];

/** Колонка зрізу в описі версії: код і тип значення. */
export type ReportColumnCommand = Schemas['ReportColumnCommand'];

/** Запит на створення опису звіту разом із першою версією. */
export type CreateReportDefRequest = Schemas['CreateReportDefRequest'];

/** Запит на створення версії-чернетки опису звіту. */
export type CreateReportVersionRequest = Schemas['CreateReportVersionRequest'];

/** Стан фонової задачі. */
export type JobStatus = Schemas['JobStatus'];

/** Задача в переліку черги — легша за {@link JobStatus}. */
export type JobSummary = Schemas['JobSummary'];

// ─────────────────────────────────────────────────────────────────────────────
// Тіла запитів.
//
// ⛔ Так само згенеровані. До наскрізного аудиту екрани складали їх
// об'єктними літералами «за здоровим глуздом», і сторож адрес їх не бачив:
// адреса була правильна, а поле — ні. Вхід надсилав `login` замість
// `userName` і отримував 400 на кожну спробу (`A7-09`).
// ─────────────────────────────────────────────────────────────────────────────

/** Сторінка проєктів. */
export type PagedProjects = Schemas['PagedResultOfProjectSummary'];

/** Проєкт у переліку. */
export type ProjectSummary = Schemas['ProjectSummary'];

/** Результат перевірки документа. */
export type ValidationResultResponse = Schemas['ValidationResultResponse'];

/** Зміна отримання алертів. */
export type SetAlertsRequest = Schemas['SetAlertsRequest'];

/** Каталог рядків інтерфейсу. */
export type UiStringCatalog = Schemas['UiStringCatalog'];

/**
 * Звіт перевірок здоров'я.
 *
 * ⛔ Тепер теж ПСЕВДОНІМ згенерованого типу. `/health/*` — middleware, а не
 * контролер, тому генератор його не бачив, і клієнт описував відповідь руками:
 * чекав `entries` словником, коли сервер писав `checks` масивом. Дашборд
 * відкривався порожнім і виглядав як здорова система (`A7-04`, `A7-36`).
 *
 * Шлях і схема додані в документ окремим трансформером (`D-137`), і `entries`
 * тут — помилка компіляції.
 */
export type HealthReport = Schemas['HealthReportDto'];

/** Одна перевірка у звіті здоров'я. */
export type HealthCheck = Schemas['HealthCheckDto'];

/** Локальний вхід. */
export type LocalLoginRequest = Schemas['LocalLoginRequest'];

/** Зміна власного пароля. */
export type ChangePasswordRequest = Schemas['ChangePasswordRequest'];

/** Запит на експорт документа. */
export type ExportRequest = Schemas['ExportRequest'];

/** Запит на застосування імпорту. */
export type ImportApplyRequest = Schemas['ImportApplyRequest'];

/** Подання або затвердження аркуша. */
export type SheetWorkflowRequest = Schemas['SheetWorkflowRequest'];

/** Дія над документом у межах одного періоду: валідація, перерахунок. */
export type DocumentPeriodRequest = Schemas['DocumentPeriodRequest'];

/** Заміна набору ресурсних грантів ролі. */
export type ReplaceGrantsRequest = Schemas['ReplaceGrantsRequest'];

/** Ресурсний грант ролі. */
export type ResourceGrantDto = Schemas['ResourceGrantDto'];

/** Публікація версії методології. */
export type PublishMethodologyRequest = Schemas['PublishMethodologyRequest'];

/** Перерахунок усього проєкту (Q-151): null-період — повний рік. */
export type ProjectRecalculationRequest = Schemas['ProjectRecalculationRequest'];

/** Прийнятий у чергу перерахунок проєкту. */
export type ProjectRecalculationAcceptedResponse = Schemas['ProjectRecalculationAcceptedResponse'];

/** Запуск збору з джерела. */
export type CollectRequest = Schemas['CollectRequest'];

/** Побудова зрізу звітності. */
export type BuildSnapshotRequest = Schemas['BuildSnapshotRequest'];

// ─────────────────────────────────────────────────────────────────────────────
// Дії, яких в інтерфейсі не було зовсім.
//
// ⛔ Не «нові можливості»: сервер умів їх від Етапу 3, а клієнт не мав до них
// жодної кнопки (`A7-39`). Із сорока дій запису дев'ятнадцять не мали
// споживача, і серед них — затвердження документа: без нього дані не стають
// дійсними (`ФВ-5.14`) і не потрапляють у звіти для регулятора (`ФВ-10.11`).
// ─────────────────────────────────────────────────────────────────────────────

/** Затвердження або відхилення аркуша; при відхиленні причина обов'язкова. */
export type ApproveSheetRequest = Schemas['ApproveSheetRequest'];

/** Повернення аркуша в роботу; причина обов'язкова (`D-67`). */
export type ReopenDocumentRequest = Schemas['ReopenDocumentRequest'];

/** Повернення періоду; причина обов'язкова, `until` обмежує вікно. */
export type ReopenPeriodRequest = Schemas['ReopenPeriodRequest'];

/** Клонування проєкту з попереднього року (`ФВ-1.3`). */
export type CloneProjectRequest = Schemas['CloneProjectRequest'];

/** Фіксація поточного періоду проєкту. */
export type SetCurrentPeriodRequest = Schemas['SetCurrentPeriodRequest'];

/** Створення рядка динамічної таблиці (`ФВ-3.2`). */
export type CreateRowRequest = Schemas['CreateRowRequest'];

/** Перегляд імпорту: зміни, конфлікти, відхилення і токен застосування. */
export type ImportPreview = Schemas['ImportPreview'];

/** Одна зміна в переліку diff імпорту. */
export type ImportChange = Schemas['ImportChange'];

/** Відхилений рядок імпорту з причиною. */
export type ImportRejection = Schemas['ImportRejection'];

/** Початок сеансу перегляду чужими правами (`ФВ-6.16`). */
export type StartSimulationRequest = Schemas['StartSimulationRequest'];

/** Вікно чинності запису реєстру (`ФВ-8.5`). */
export type SetValidityRequest = Schemas['SetValidityRequest'];

/** Клонування версії шаблону. */
export type CloneVersionRequest = Schemas['CloneVersionRequest'];

/** Публікація версії шаблону; причина обов'язкова (той самий патерн, що й методологія). */
export type PublishVersionRequest = Schemas['PublishVersionRequest'];

/** Виведення версії шаблону з обігу (`ФВ-7.8`). */
export type DeprecateVersionRequest = Schemas['DeprecateVersionRequest'];

/** Різниця двох версій шаблону разом із кількістю зачеплених документів. */
export type TemplateDiffDto = Schemas['TemplateDiffDto'];

/** Прогін методології без запису результату (`ФВ-13.5`). */
export type SimulateMethodologyRequest = Schemas['SimulateMethodologyRequest'];

/** Результат симуляції: виходи, розбіжність із опублікованим, трасування. */
export type SimulationResultDto = Schemas['SimulationResultDto'];

/** Зміна рядка інтерфейсу (`ФВ-14.9`). */
export type SetUiStringRequest = Schemas['SetUiStringRequest'];

/** Область видимості рядка каталогу: публічна чи приватна (`D-114`). */
export type UiStringScope = Schemas['UiStringScope'];

/** Нова ревізія каталогу після зміни рядка. */
export type UiStringRevisionResponse = Schemas['UiStringRevisionResponse'];

/** Нова ревізія презентаційного шару після патча. */
export type PresentationRevisionResponse = Schemas['PresentationRevisionResponse'];

/** Скільки записів зачепила зміна вікна чинності. */
export type AffectedRowsResponse = Schemas['AffectedRowsResponse'];

/** Мова інтерфейсу з реєстру (`ФВ-14.9`). */
export type LanguageDto = Schemas['LanguageDto'];

/** Одиниця вимірювання з довідника. */
export type UnitRef = Schemas['UnitRef'];

/** Запит на конверсію значення між одиницями. */
export type ConvertUnitRequest = Schemas['ConvertUnitRequest'];

/** Результат конверсії. */
export type ConvertUnitResponse = Schemas['ConvertUnitResponse'];

/** Зміна комірки в журналі аудиту. */
export type CellChangeView = Schemas['CellChangeView'];

/** Сторінка журналу змін. */
export type CellChangePage = Schemas['PagedResultOfCellChangeView'];

/**
 * Колонка у структурі шаблону — з **усіма** мовами заголовка.
 *
 * ⚠ Не `ColumnDto`: та описує колонку в зрізі документа і несе один
 * локалізований рядок. Редактор презентації має бачити всі переклади, інакше
 * правка англійського підпису стирала б решту (`ФВ-7.2`).
 */
export type TemplateColumnDto = Schemas['TemplateColumnDto'];

/** Аркуш у структурі версії. */
export type SheetDto = Schemas['SheetDto'];

/** Таблиця у структурі версії. */
export type TableDto = Schemas['TableDto'];

// ─────────────────────────────────────────────────────────────────────────────
// Створення сутностей.
//
// ⛔ Сім дій створення були недосяжні (`A7-42`), і сторож їх не бачив: він
// порівнював самі шляхи, тому `POST /templates` вважався покритим тим, що
// клієнт ЧИТАЄ `GET /templates`. Система не мала способу завести ані проєкт,
// ані шаблон, ані документ, ані користувача.
// ─────────────────────────────────────────────────────────────────────────────

/** Створення шаблону. */
export type CreateTemplateRequest = Schemas['CreateTemplateRequest'];

/** Створення версії шаблону; `cloneFromVersionId` дає клон замість порожньої. */
export type CreateTemplateVersionRequest = Schemas['CreateTemplateVersionRequest'];

/** Створення проєкту разом із календарем періодів. */
export type CreateProjectRequest = Schemas['CreateProjectRequest'];

/** Створення документа зі складом аркушів (`ФВ-3.2`). */
export type CreateDocumentRequest = Schemas['CreateDocumentRequest'];

/** Створення ролі з набором прав. */
export type CreateRoleRequest = Schemas['CreateRoleRequest'];

/** Створення користувача; локальний пароль — разовий (`ФВ-6.18`). */
export type CreateUserRequest = Schemas['CreateUserRequest'];

/** Запис довідника: створення або зміна. */
export type RegistryEntryUpsertDto = Schemas['RegistryEntryUpsertDto'];

/** Ідентифікатор створеного або зміненого запису довідника. */
export type RegistryEntryIdResponse = Schemas['RegistryEntryIdResponse'];

// ─────────────────────────────────────────────────────────────────────────────
// Тіла відповідей на створення.
//
// ⛔ Чотирнадцять дій сервера повертали АНОНІМНИЙ об'єкт, тому в схемі на
// їхньому місці лишалося порожнє тіло — і клієнту не було з чого зробити
// псевдонім. Обхідним шляхом стала форма на місці виклику
// (`apiFetch<{ projectId: number }>`), тобто рівно те, від чого захищає
// `D-137`: помилка в назві поля дає `undefined` там, де компілятор обіцяв
// число, і жоден тип цього не помітить (`A7-44`).
// ─────────────────────────────────────────────────────────────────────────────

/** Створений проєкт. */
export type ProjectIdResponse = Schemas['ProjectIdResponse'];

/** Створений шаблон. */
export type TemplateIdResponse = Schemas['TemplateIdResponse'];

/** Створена версія шаблону. */
export type VersionIdResponse = Schemas['VersionIdResponse'];

/** Створений документ. */
export type DocumentIdResponse = Schemas['DocumentIdResponse'];

/** Створена роль. */
export type RoleIdResponse = Schemas['RoleIdResponse'];

/** Створений користувач; ані пароля, ані хеша тут немає (`ФВ-6.11`). */
export type UserIdResponse = Schemas['UserIdResponse'];

/** Створений рядок динамічної таблиці. */
export type RowKeyResponse = Schemas['RowKeyResponse'];

/** Розпочатий сеанс симуляції разом із суб'єктом і прапорцем «лише читання». */
export type SimulationSessionResponse = Schemas['SimulationSessionResponse'];

/** Прийнята в чергу довга операція. */
export type JobAcceptedResponse = Schemas['JobAcceptedResponse'];

/** Зауваження до виразу: код, текст і позиція в тексті. */
export type DiagnosticInfo = Schemas['DiagnosticInfo'];

/** Результат перевірки виразу при введенні (`ФВ-9.15a`). */
export type ExpressionValidationDto = Schemas['ExpressionValidationDto'];

/** Склад мови виразів для діалекту й контексту. */
export type ExpressionMetadataDto = Schemas['ExpressionMetadataDto'];

/** Функція діалекту та її сигнатура. */
export type ExpressionFunctionDto = Schemas['ExpressionFunctionDto'];

/** Символ, на який може посилатися вираз: `CST.`, `!`, `@`, `HDR.`. */
export type ExpressionSymbolDto = Schemas['ExpressionSymbolDto'];

/** Тіло запиту на перевірку виразу. */
export type ValidateExpressionBody = Schemas['ValidateExpressionBody'];

/**
 * Діалект мови виразів (`D-113`).
 *
 * ⚠ Тип **згенерований** із серверного переліку, а не написаний тут. Коли до
 * нього додасться режим C# (`K-1`), тип розшириться сам, і TypeScript змусить
 * обробити новий випадок — замість того, щоб мовчки його не показати.
 */
export type ExpressionDialect = Schemas['ExpressionDialect'];


/** Вердикт одного тесту золотого набору (`ФВ-13.7`). */
export type TestCaseVerdict = Schemas['TestCaseVerdict'];

/**
 * Перегляд мапінгу на реальних рядках джерела (`ФВ-13.14`).
 *
 * ⛔ Тип несе не лише зв'язки, що зійшлися: `unmappedSourceFields` і
 * `uncoveredColumns` — це **розриви**, і саме заради них перегляд існує.
 */
export type MappingPreview = Schemas['MappingPreview'];

/** Реальний рядок джерела разом з адресою, куди він лягає. */
export type MappingPreviewRow = Schemas['MappingPreviewRow'];

/** Підсумок одного мапінгу: що саме він поклав би в комірку. */
export type MappedFieldPreview = Schemas['MappedFieldPreview'];

/** Поле джерела, яке не лягає нікуди. */
export type UnmappedSourceField = Schemas['UnmappedSourceField'];

/** Колонка документа, за якою не стоїть нічого. */
export type UncoveredColumn = Schemas['UncoveredColumn'];

/**
 * Що станеться з рядком джерела або з мапінгом.
 *
 * ⚠ Тип **згенерований** із серверного переліку: новий різновид розриву
 * змусить TypeScript обробити випадок, а не мовчки його не показати.
 */
export type MappingOutcome = Schemas['MappingOutcome'];

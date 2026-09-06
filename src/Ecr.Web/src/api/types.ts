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

/** Повідомлення валідації. */
export type ValidationMessageDto = Schemas['ValidationMessageDto'];

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
export type ApprovalRouteDto = Schemas['ApprovalRouteDto'];
export type AffectedRolesResponse = Schemas['AffectedRolesResponse'];
export type AffectedStepsResponse = Schemas['AffectedStepsResponse'];
export type AccessMatrixDto = Schemas['AccessMatrixDto'];
export type AccessMatrixSheetDto = Schemas['AccessMatrixSheetDto'];
export type AccessMatrixCellDto = Schemas['AccessMatrixCellDto'];

/** Довідник. */
export type RegistryDefDto = Schemas['RegistryDefDto'];
export type RegistrySourceKind = Schemas['RegistrySourceKind'];

/** Запис довідника. */
export type RegistryEntryDto = Schemas['RegistryEntryDto'];

/** Методологія з версіями. */
export type MethodologyDto = Schemas['MethodologyDto'];

/** Версія методології. */
export type MethodologyVersionDto = Schemas['MethodologyVersionDto'];

/** Роль із оголошеними правами. */
export type RoleView = Schemas['RoleView'];

/** Користувач; ані хеша пароля, ані солі тут немає за побудовою (ФВ-6.11). */
export type UserView = Schemas['UserView'];

/** Сторінка користувачів. */
export type UserPage = Schemas['PagedResultOfUserView'];

/** Календар періодів проєкту. */
export type PeriodCalendarDto = Schemas['PeriodCalendarDto'];

/** Період проєкту. */
export type PeriodDto = Schemas['PeriodDto'];

/** Сутність збору зі станом останнього прогону і прогалиною. */
export type SourceEntityStatus = Schemas['SourceEntityStatus'];

/** Зріз звітності. */
export type ReportSnapshotSummary = Schemas['ReportSnapshotSummary'];

/** Стан фонової задачі. */
export type JobStatus = Schemas['JobStatus'];

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

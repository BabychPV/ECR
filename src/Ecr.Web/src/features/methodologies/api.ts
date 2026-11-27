import { apiFetch } from '@/api/client';
import type { components } from '@/api/schema';
import type {
  CalculationBindingDto,
  CalculationLevel,
  CalculationResultDto,
  CreateMethodologyRequest,
  CreateMethodologyVersionRequest,
  MethodologyDraftVersionDto,
  MethodologyPublicationDiff,
  MethodologyRequiredInputDto,
  DerivedRequiredInputsDto,
  MethodologySummaryDto,
  MethodologyTestCaseDto,
  MethodologyTraceStepsDto,
  PublishMethodologyRequest,
  SaveCalculationBindingRequest,
  SaveMethodologyRequiredInputRequest,
  SaveMethodologyTestCaseRequest,
  SetMethodologyModesRequest,
  SetTraceStepsRequest,
} from '@/api/types';

/*
 * ⛔ Адреса в КОЖНІЙ функції записана повністю, а не збирається з помічника.
 * Це не багатослівність: сторож `Кожна_дія_сервера_має_споживача_в_інтерфейсі`
 * шукає в коді клієнта саме літерали `/api/v1/…` разом із методом поруч.
 * Винесений у помічник префікс зробив би половину дій сервера «недосяжними з
 * інтерфейсу» — тобто сторож почав би вимагати кнопок, які вже є.
 */

/**
 * Звернення конфігуратора методологій (`ФВ-9.15`).
 *
 * ⛔ Читання тут **не те саме**, що `GET /api/v1/methodologies`. Той перелік
 * віддає лише опубліковані версії — те, чим рахують. Конфігуратор питає про
 * те, що правлять, а правити можна лише чернетку, тож йому потрібні і вона
 * теж. Спроба обійтися одним переліком закінчилася б тим, що кнопка
 * «Опублікувати» стоїть для версій, яких у переліку немає за побудовою.
 */

/** Усі версії методології, включно з чернетками. */
export function methodologyVersions(
  methodologyId: number,
): Promise<MethodologyDraftVersionDto[]> {
  return apiFetch<MethodologyDraftVersionDto[]>(
    `/api/v1/methodologies/${String(methodologyId)}/versions`,
  );
}

/**
 * Створює версію-чернетку: клон наявної або порожню.
 *
 * ⛔ Клон — **єдиний спосіб змінити опубліковану версію** (`ФВ-9.1`): вона
 * незмінна, бо на її числа посилаються вже подані форми.
 */
export function createMethodologyVersion(
  methodologyId: number,
  body: {
    readonly versionNumber: string;
    readonly copyFromVersionId: number | null;
    readonly level: CalculationLevel;
    /** ⛔ Явно і лише скрипт (✎ 24.11: формульний рушій виведено з інтерфейсу). */
    readonly engine: 'Script';
  },
): Promise<MethodologyDraftVersionDto> {
  return apiFetch<MethodologyDraftVersionDto>(
    `/api/v1/methodologies/${String(methodologyId)}/versions`,
    {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        versionNumber: body.versionNumber,
        copyFromVersionId: body.copyFromVersionId,
        level: body.level,
        engine: body.engine,
      } satisfies CreateMethodologyVersionRequest),
    },
  );
}

/**
 * Публікує версію-чернетку. Право `Calculation.Publish` — **небезпечне**
 * (`sec.Permission.IsDangerous`), вбудованим ролям seed-ом не видається
 * (`PublishMethodologyHandler`).
 *
 * ⛔ Причина зміни й дата набуття чинності обов'язкові на сервері
 * (`ECR-CALC-0422`, ФВ-14.7): без дати незрозуміло, які періоди рахувати цією
 * версією, а які — попередньою.
 */
export function publishMethodologyVersion(
  methodologyId: number,
  versionId: number,
  body: PublishMethodologyRequest,
): Promise<MethodologyPublicationDiff> {
  return apiFetch<MethodologyPublicationDiff>(
    `/api/v1/methodologies/${String(methodologyId)}/versions/${String(versionId)}/publish`,
    {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    },
  );
}

/**
 * Заводить методологію-контейнер — **без жодної версії**.
 *
 * ⛔ Перша версія створюється окремою дією, і це не зайвий крок: у неї свій
 * рівень драбини виразності і свої режими обчислення, тож склеїти обидві дії
 * означало б ухвалити ці рішення за методолога в момент, коли він ще навіть не
 * назвав методологію.
 */
export function createMethodology(body: CreateMethodologyRequest): Promise<MethodologySummaryDto> {
  return apiFetch<MethodologySummaryDto>('/api/v1/methodologies', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
}

/**
 * Обов'язкові вхідні колонки версії (директива «обов'язкові вхідні колонки
 * методології», gate перед збереженням клітинки).
 */
export function methodologyRequiredInputs(
  methodologyId: number,
  versionId: number,
): Promise<MethodologyRequiredInputDto[]> {
  return apiFetch<MethodologyRequiredInputDto[]>(
    `/api/v1/methodologies/${String(methodologyId)}/versions/${String(versionId)}/required-inputs`,
  );
}

/**
 * ✎ D-578: виведені обов'язкові входи версії — поля, які читає скрипт (лише читання), з винятком методолога
 * (Block/Warn/Optional) з переліку `required-inputs`. `status` ≠ `available` — автоматичного переліку немає.
 */
export function methodologyDerivedInputs(
  methodologyId: number,
  versionId: number,
): Promise<DerivedRequiredInputsDto> {
  return apiFetch<DerivedRequiredInputsDto>(
    `/api/v1/methodologies/${String(methodologyId)}/versions/${String(versionId)}/derived-inputs`,
  );
}

/**
 * Заводить або змінює обов'язкову вхідну колонку.
 *
 * ⚠ Ключ — `ColumnDefId`, а не код: на відміну від правил і виходів, ця
 * вимога не має природного коду, унікальність тримається на парі
 * (версія, колонка).
 */
export function saveMethodologyRequiredInput(
  methodologyId: number,
  versionId: number,
  columnDefId: number,
  body: SaveMethodologyRequiredInputRequest,
): Promise<MethodologyRequiredInputDto> {
  return apiFetch<MethodologyRequiredInputDto>(
    `/api/v1/methodologies/${String(methodologyId)}/versions/${String(versionId)}/required-inputs/${String(columnDefId)}`,
    { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) },
  );
}

/** Прибирає обов'язкову вхідну колонку чернетки (✎ P-1124-1: «list/edit/remove»). */
export function deleteMethodologyRequiredInput(
  methodologyId: number,
  versionId: number,
  columnDefId: number,
): Promise<void> {
  return apiFetch<void>(
    `/api/v1/methodologies/${String(methodologyId)}/versions/${String(versionId)}/required-inputs/${String(columnDefId)}`,
    { method: 'DELETE' },
  );
}

/** Золотий набір версії (`ФВ-13.7`). */
export function methodologyTestCases(
  methodologyId: number,
  versionId: number,
): Promise<MethodologyTestCaseDto[]> {
  return apiFetch<MethodologyTestCaseDto[]>(
    `/api/v1/methodologies/${String(methodologyId)}/versions/${String(versionId)}/tests`,
  );
}

/**
 * Записує тест золотого набору.
 *
 * ⛔ Не «тести заради тестів», а умова публікації (`ФВ-9.12`): порожній набір
 * НЕ зелений, і версія без нього не публікується взагалі.
 */
export function saveMethodologyTestCase(
  methodologyId: number,
  versionId: number,
  code: string,
  body: SaveMethodologyTestCaseRequest,
): Promise<MethodologyTestCaseDto> {
  return apiFetch<MethodologyTestCaseDto>(
    `/api/v1/methodologies/${String(methodologyId)}/versions/${String(versionId)}/tests/${encodeURIComponent(code)}`,
    { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) },
  );
}

/**
 * Задає режими обчислення чернетки.
 *
 * ⛔ Обидва перші режими тихо змінюють **усі** числа версії, не змінивши жодної
 * формули (`ФВ-9.9`, `ФВ-16.11`). Тому це окрема дія, а не поле у створенні
 * версії, і тому обидва обов'язкові в diff публікації (`D-78`).
 */
export function saveMethodologyModes(
  methodologyId: number,
  versionId: number,
  body: SetMethodologyModesRequest,
): Promise<MethodologyDraftVersionDto> {
  return apiFetch<MethodologyDraftVersionDto>(
    `/api/v1/methodologies/${String(methodologyId)}/versions/${String(versionId)}/modes`,
    { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) },
  );
}

/**
 * Прапор методології «трасувати проміжні кроки» (`D-565`, PERF-03).
 *
 * ⚠ Живе на МЕТОДОЛОГІЇ, а не на версії: чисел не змінює, тож вмикається без чернетки й публікації.
 */
export function methodologyTraceSteps(methodologyId: number): Promise<MethodologyTraceStepsDto> {
  return apiFetch<MethodologyTraceStepsDto>(`/api/v1/methodologies/${String(methodologyId)}/trace-steps`);
}

/** Вмикає чи вимикає запис проміжних кроків методології (`D-565`). */
export function saveMethodologyTraceSteps(
  methodologyId: number,
  body: SetTraceStepsRequest,
): Promise<MethodologyTraceStepsDto> {
  return apiFetch<MethodologyTraceStepsDto>(
    `/api/v1/methodologies/${String(methodologyId)}/trace-steps`,
    { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) },
  );
}

/**
 * Прив'язки методології до колонок документів.
 *
 * ⚠ Прив'язка живе на МЕТОДОЛОГІЇ, а не на її версії: вона переживає всі версії
 * одразу і клонуванням не копіюється. Тому в адресі немає версії.
 */
export function calculationBindings(methodologyId: number): Promise<CalculationBindingDto[]> {
  return apiFetch<CalculationBindingDto[]>(
    `/api/v1/methodologies/${String(methodologyId)}/bindings`,
  );
}

/**
 * Прив'язує вихід методології до колонки документа.
 *
 * ⛔ Без прив'язки перерахунок документа завершується успіхом і не рахує
 * нічого: планувальник бере методології саме з `cfg.CalculationBinding`, а
 * порожній набір прив'язок помилкою не є.
 *
 * ⚠ Таблиця в запиті НЕ передається — вона виводиться з колонки: два поля про
 * те саме розходяться мовчки, а прив'язка з чужою таблицею просто не
 * спрацьовує.
 */
export function saveCalculationBinding(
  methodologyId: number,
  columnDefId: number,
  outputCode: string,
  body: SaveCalculationBindingRequest,
): Promise<CalculationBindingDto> {
  return apiFetch<CalculationBindingDto>(
    `/api/v1/methodologies/${String(methodologyId)}/bindings/${String(columnDefId)}/${encodeURIComponent(outputCode)}`,
    { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) },
  );
}

/**
 * Числа, які дав актуальний прогін розрахунку на документі.
 *
 * ⛔ Окреме читання, а не поле зрізу таблиці, і це `D-69`: результат методології
 * не потрапляє в `doc.CellValue` — у документ він приходить посиланням через
 * `cfg.CalculationBinding`. Доти побачити це число не було де взагалі.
 */
export function calculationResults(
  documentId: number,
  periodKey: number,
): Promise<CalculationResultDto[]> {
  return apiFetch<CalculationResultDto[]>(
    `/api/v1/documents/${String(documentId)}/calculation-results?periodKey=${String(periodKey)}`,
  );
}

/** Покриття версії (`BE-25`): вихід → колонки, і колонки, що чекають на вихід, якого версія не оголошує. */
export type MethodologyCoverageDto = components['schemas']['MethodologyCoverageDto'];

/** Різниця двох версій (`BE-25`): формули, константи, тести. */
export type MethodologyVersionDiffDto = components['schemas']['MethodologyVersionDiffDto'];

/** Матриця покриття версії. Вимкнені прив'язки сервер не рахує ні покриттям, ні очікуванням. */
export function methodologyCoverage(
  methodologyId: number,
  versionId: number,
): Promise<MethodologyCoverageDto> {
  return apiFetch<MethodologyCoverageDto>(
    `/api/v1/methodologies/${String(methodologyId)}/versions/${String(versionId)}/coverage`,
  );
}

/** Що змінилося у версії `versionId` порівняно з `baseVersionId` тієї самої методології. */
export function methodologyVersionDiff(
  methodologyId: number,
  versionId: number,
  baseVersionId: number,
): Promise<MethodologyVersionDiffDto> {
  return apiFetch<MethodologyVersionDiffDto>(
    `/api/v1/methodologies/${String(methodologyId)}/versions/${String(versionId)}/diff?baseVersionId=${String(baseVersionId)}`,
  );
}

/**
 * Видаляє версію-чернетку.
 *
 * ⚠ Опублікована, виведена з обігу або вже використана в розрахунку — `409`
 * `ECR-CALC-0409`, причина в `reason` (`Published`, `Deprecated`, `UsedInCalculations`).
 */
export function deleteMethodologyVersion(methodologyId: number, versionId: number): Promise<void> {
  return apiFetch<void>(
    `/api/v1/methodologies/${String(methodologyId)}/versions/${String(versionId)}`,
    { method: 'DELETE' },
  );
}

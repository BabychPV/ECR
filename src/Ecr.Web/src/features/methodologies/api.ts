import { apiFetch } from '@/api/client';
import type {
  CalculationBindingDto,
  CalculationLevel,
  CalculationResultDto,
  CreateMethodologyRequest,
  CreateMethodologyVersionRequest,
  MethodologyConstantDto,
  MethodologyDraftVersionDto,
  MethodologyFormulaDto,
  MethodologyOutputDto,
  MethodologyRuleDto,
  MethodologySummaryDto,
  MethodologyTestCaseDto,
  SaveCalculationBindingRequest,
  SaveMethodologyConstantRequest,
  SaveMethodologyFormulaRequest,
  SaveMethodologyOutputRequest,
  SaveMethodologyRuleRequest,
  SaveMethodologyTestCaseRequest,
  SetMethodologyModesRequest,
} from '@/api/types';
import { formulaBody, type FormulaDraft } from './draft';

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

/** Формули версії в порядку обчислення. */
export function methodologyFormulas(
  methodologyId: number,
  versionId: number,
): Promise<MethodologyFormulaDto[]> {
  return apiFetch<MethodologyFormulaDto[]>(
    `/api/v1/methodologies/${String(methodologyId)}/versions/${String(versionId)}/formulas`,
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
      } satisfies CreateMethodologyVersionRequest),
    },
  );
}

/**
 * Записує формулу чернетки; створює її, якщо коду ще немає.
 *
 * ⚠ `PUT`, а не `POST`: адресою формули є її **код** у межах версії — те, чим
 * на неї посилаються вирази (`!Name`). Тому створення й зміна — одна дія, і
 * повторний запит із тим самим тілом дає той самий стан.
 *
 * ⚠ Код іде через `encodeURIComponent`: у ньому дозволені лише латиниця,
 * цифри й підкреслення (`EcrCode`), але покладатися на це в побудові адреси
 * означало б, що перша ж послаблена перевірка коду ламає маршрутизацію мовчки.
 */
export function saveMethodologyFormula(
  methodologyId: number,
  draft: FormulaDraft,
): Promise<MethodologyFormulaDto> {
  const body: SaveMethodologyFormulaRequest = formulaBody(draft);

  return apiFetch<MethodologyFormulaDto>(
    `/api/v1/methodologies/${String(methodologyId)}/versions/${String(draft.versionId)}/formulas/${encodeURIComponent(draft.code)}`,
    {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    },
  );
}

/** Прибирає формулу з чернетки. */
export function deleteMethodologyFormula(
  methodologyId: number,
  versionId: number,
  code: string,
): Promise<void> {
  return apiFetch<void>(
    `/api/v1/methodologies/${String(methodologyId)}/versions/${String(versionId)}/formulas/${encodeURIComponent(code)}`,
    { method: 'DELETE' },
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

/** Константи версії — усі, включно з мітками категорій. */
export function methodologyConstants(
  methodologyId: number,
  versionId: number,
): Promise<MethodologyConstantDto[]> {
  return apiFetch<MethodologyConstantDto[]>(
    `/api/v1/methodologies/${String(methodologyId)}/versions/${String(versionId)}/constants`,
  );
}

/**
 * Записує константу чернетки; створює її, якщо коду ще немає.
 *
 * ⚠ `validTo` — перший НЕчинний день (виключна межа): коефіцієнт, чинний увесь
 * 2024 рік, має тут `2025-01-01`.
 */
export function saveMethodologyConstant(
  methodologyId: number,
  versionId: number,
  code: string,
  body: SaveMethodologyConstantRequest,
): Promise<MethodologyConstantDto> {
  return apiFetch<MethodologyConstantDto>(
    `/api/v1/methodologies/${String(methodologyId)}/versions/${String(versionId)}/constants/${encodeURIComponent(code)}`,
    { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) },
  );
}

/** Правила відбору рядків версії — включно з вимкненими. */
export function methodologyRules(
  methodologyId: number,
  versionId: number,
): Promise<MethodologyRuleDto[]> {
  return apiFetch<MethodologyRuleDto[]>(
    `/api/v1/methodologies/${String(methodologyId)}/versions/${String(versionId)}/rules`,
  );
}

/**
 * Записує правило відбору рядків документа.
 *
 * ⚠ Предикат **структурований**, а не вираз: діалект методологій посилань на
 * комірки документів не має за побудовою (`Q-026`). «Уся таблиця» пишеться як
 * `{}` — і саме тому таке правило має найнижчий пріоритет: інакше воно
 * перекрило б усі точніші (`ФВ-13.4`).
 */
export function saveMethodologyRule(
  methodologyId: number,
  versionId: number,
  code: string,
  body: SaveMethodologyRuleRequest,
): Promise<MethodologyRuleDto> {
  return apiFetch<MethodologyRuleDto>(
    `/api/v1/methodologies/${String(methodologyId)}/versions/${String(versionId)}/rules/${encodeURIComponent(code)}`,
    { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) },
  );
}

/** Оголошені виходи версії. */
export function methodologyOutputs(
  methodologyId: number,
  versionId: number,
): Promise<MethodologyOutputDto[]> {
  return apiFetch<MethodologyOutputDto[]>(
    `/api/v1/methodologies/${String(methodologyId)}/versions/${String(versionId)}/outputs`,
  );
}

/**
 * Оголошує вихід версії.
 *
 * ⛔ Без жодного виходу методологія рахує всі формули і не записує **нічого**:
 * цикл запису результату йде по оголошених виходах, а не по формулах.
 */
export function saveMethodologyOutput(
  methodologyId: number,
  versionId: number,
  code: string,
  body: SaveMethodologyOutputRequest,
): Promise<MethodologyOutputDto> {
  return apiFetch<MethodologyOutputDto>(
    `/api/v1/methodologies/${String(methodologyId)}/versions/${String(versionId)}/outputs/${encodeURIComponent(code)}`,
    { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) },
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

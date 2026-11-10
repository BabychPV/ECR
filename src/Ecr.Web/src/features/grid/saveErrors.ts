import { EcrApiError, type RequiredInputCell } from '@/api/client';
import type { TableSliceDto } from '@/api/types';
import type { PendingEdit } from './useCellPatch';
import { problemText } from '@/shared/ui/problemText';
import { decide, denyText } from './permissions';
import { columnIndexOf } from './rowIndex';

/**
 * Що з відмови збереження можна показати людині (`ФВ-14.9a`).
 *
 * ⛔ Не `error.message`: це `detail ?? title` БЕЗ розбору мови, тобто для
 * відмови без `messageKey` — речення розробника українською, якої в продукті
 * немає (`D-95`). Подробиця береться лише локалізована сервером, інакше —
 * назва проблеми з каталогу (`problemText`).
 */
export function refusalText(error: unknown): string {
  const shown = problemText(error);

  return shown.detail ?? shown.title;
}

/**
 * Комірки, яких стосується конкретна відмова збереження (Q-30x, High).
 *
 * ⛔ До цього виправлення справжня причина відмови (наприклад, `Колонка «C1»
 * очікує число.` — `ECR-CELL-0422`) доїжджала до клієнта коректно, але ніде
 * не показувалась: тулбар малював лише заглушку «NOT SAVED — SEE THE ERROR
 * ABOVE», а сам текст губився в необробленому знеструмленні проміса
 * (`Uncaught (in promise)`), яке бачить лише консоль розробника, а не
 * оператор. `DocumentGrid.save` показує `refusalText(error)` в `Alert` — ця
 * функція вирішує ДРУГУ частину: яку саме комірку підсвітити маркером,
 * аналогічним `.ecr-cell-required-input-blocked` (Q-306).
 *
 * ⚠ Сервер повертає причину у двох формах, і обидві — РЕАЛЬНІ дані, не
 * здогад:
 *
 *  1. Пакетна бізнес-валідація (`PatchCellsHandler.EnsureValidationPasses`,
 *     `ECR-CELL-0422`/`ECR-CALC-0437`) кладе готовий перелік `cells:
 *     [{rowKey, columnCode, ruleCode, message}]` — те саме розширення, що
 *     вже читає `EcrApiError.requiredInputCells` для `ECR-CALC-0437`.
 *  2. Відмова читання значення комірки (`CellValueReader.Mismatch`,
 *     також `ECR-CELL-0422`, саме вона стоїть за «Колонка «C1» очікує
 *     число.») трапляється РАНІШЕ, до збирання списку рядків, і несе лише
 *     `columnCode` — без `rowKey`. Рядок тут береться НЕ здогадом, а з
 *     клітинок, які клієнт САМ щойно намагався записати цим самим патчем
 *     (`attempted`): це дані, які вже є в нас, а не вигадані.
 *
 * ⛔ Якщо жодної з двох форм немає (третій код помилки, якого ця функція не
 * знає), повертається порожній список: краще НІЯКОГО маркера на комірці, ніж
 * маркер на комірці, якої відмова могла не стосуватися взагалі.
 */
export function cellsOfSaveError(
  error: EcrApiError,
  attempted: readonly PendingEdit[],
): readonly RequiredInputCell[] {
  const cells = error.problem.extensions2?.['cells'];
  if (Array.isArray(cells)) {
    return cells as RequiredInputCell[];
  }

  const columnCode = error.problem.extensions2?.['columnCode'];
  if (typeof columnCode !== 'string') {
    return [];
  }

  return attempted
    .filter((edit) => edit.columnCode === columnCode)
    .map((edit) => ({
      rowKey: edit.rowKey,
      columnCode,
      ruleCode: error.problem.errorCode,
      message: refusalText(error),
    }));
}

/** Причина відмови для однієї названої комірки (D2, приймальна Land 12.10). */
export interface RejectedCellReason {
  readonly rowKey: string;
  readonly columnCode: string;
  readonly message: string;
}

/**
 * Скільки причин максимум виводить банер збереження; решта — «…».
 */
export const SaveErrorReasonsLimit = 5;

/**
 * Причини, які сервер назвав у тілі відмови (`cells[].message`, `ECR-CELL-0422`).
 *
 * ⛔ D2: банер казав лише «Validation rejected the save: 1 cell(s) with an error» —
 * правило («Value must be <= 672») доїжджало в тілі, але не показувалось. Беруться
 * лише комірки з іменем колонки й непорожнім текстом; однакові пари зводяться.
 */
export function rejectedCellReasons(error: unknown): readonly RejectedCellReason[] {
  if (!(error instanceof EcrApiError)) return [];

  const cells = error.problem.extensions2?.['cells'];
  if (!Array.isArray(cells)) return [];

  const seen = new Set<string>();
  const result: RejectedCellReason[] = [];

  for (const raw of cells as unknown[]) {
    if (typeof raw !== 'object' || raw === null) continue;
    const { rowKey, columnCode, message } = raw as Record<string, unknown>;
    if (typeof columnCode !== 'string' || typeof message !== 'string' || message.trim().length === 0) continue;

    const row = typeof rowKey === 'string' ? rowKey : '';
    const key = `${row}:${columnCode}:${message}`;
    if (seen.has(key)) continue;

    seen.add(key);
    result.push({ rowKey: row, columnCode, message });
  }

  return result;
}

/**
 * Причини відмови `403 ECR-ACCS-0403 deniedCells` по комірках (живий прохід 10.11).
 *
 * ⛔ Банер казав лише «Cells you may not edit in this batch: 1. Reason for the first: CalculatedCell» — без колонки й
 * рядка, з технічною назвою причини. Сервер не перелічує комірки (лише `deniedCount` і `reason` першої), тож:
 *  - комірки пакета, які сітка сама вважає забороненими (`decide`), — з текстом своєї причини (той самий, що в підказці
 *    сірої комірки);
 *  - інакше, якщо в пакеті рівно одна комірка, — вона, з текстом серверної причини (`denyText(reason)`);
 *  - інакше нічого: краще без переліку, ніж назвати комірку, якої відмова могла не стосуватися.
 */
export function deniedCellReasons(
  error: unknown,
  attempted: readonly PendingEdit[],
  slice: TableSliceDto | undefined,
): readonly RejectedCellReason[] {
  if (!(error instanceof EcrApiError) || error.problem.errorCode !== 'ECR-ACCS-0403') return [];

  const reason = error.problem.extensions2?.['reason'];
  // ⚠ `ConfirmationRequired` — не заборона, а прохання підтвердити (окремий діалог, `#43`).
  if (typeof reason !== 'string' || reason === 'ConfirmationRequired') return [];

  if (slice !== undefined) {
    const columns = columnIndexOf(slice);
    const local = attempted.flatMap((edit) => {
      const column = columns.get(edit.columnCode);
      if (column === undefined) return [];

      const decision = decide(slice, edit.rowKey, column);

      return decision.editable ? [] : [{ rowKey: edit.rowKey, columnCode: edit.columnCode, message: decision.hint }];
    });
    if (local.length > 0) return local;
  }

  const [only] = attempted;
  if (attempted.length !== 1 || only === undefined) return [];

  return [{ rowKey: only.rowKey, columnCode: only.columnCode, message: denyText(reason) ?? refusalText(error) }];
}

/** Скільки комірок відхилено (`deniedCount` відмови), інакше — скільки названо. */
export function deniedCountOf(error: unknown, named: number): number {
  const raw = error instanceof EcrApiError ? error.problem.extensions2?.['deniedCount'] : undefined;
  const count = typeof raw === 'string' || typeof raw === 'number' ? Number(raw) : Number.NaN;

  return Number.isInteger(count) && count > 0 ? count : named;
}

/**
 * Правки обчислюваних комірок (`Formula`/`Calculated`), яких сітка не дає писати (`decide` → `CalculatedCell`).
 *
 * ⛔ Такий `PATCH` сервер відхиляє ЦІЛИМ пакетом (`403 deniedCells`) — разом із правильними правками поруч. Правка
 * сюди могла потрапити повз редактор (відновлення з браузера, комірка свіжого динамічного рядка до приходу зрізу),
 * тож перед надсиланням вона відділяється: лишається позначеною й незбереженою, з причиною, а на сервер не йде.
 * ⚠ Ручне значення формули (`D-461`, `overridesFormula`) `decide` дозволяє — його це не зачіпає.
 */
export function computedEditsOf(
  slice: TableSliceDto | undefined,
  edits: readonly PendingEdit[],
): { readonly send: PendingEdit[]; readonly blocked: { edit: PendingEdit; message: string }[] } {
  if (slice === undefined) return { send: [...edits], blocked: [] };

  const columns = columnIndexOf(slice);
  const send: PendingEdit[] = [];
  const blocked: { edit: PendingEdit; message: string }[] = [];

  for (const edit of edits) {
    const column = columns.get(edit.columnCode);
    const computed = column !== undefined && (column.dataType === 'Formula' || column.dataType === 'Calculated');
    const decision = computed ? decide(slice, edit.rowKey, column) : null;

    if (decision !== null && decision.reason === 'CalculatedCell') blocked.push({ edit, message: decision.hint });
    else send.push(edit);
  }

  return { send, blocked };
}

/**
 * Які правки пакета ТРИМАТИ після відмови (`V-01`) — і чи тримати взагалі.
 *
 * ⛔ Тримаються лише відмови, які повторення НЕ вилікує: невірне значення
 * (`422`), заборона (`403`), кривий запит (`400`) і конфлікт версії рядка
 * (`409`). Мережа, `5xx`, `429`, `401` і `409 ECR-DOC-4091 lockTimeout` — минущі: наступний пакет має везти ті
 * самі правки, і тримати їх означало б кинути правильні дані через збій
 * зв'язку.
 *
 * ⛔ `409` доти НЕ тримався — і конфліктна правка (зі старою `baseVersion`)
 * їхала з кожним наступним пакетом, який сервер знову відхиляв цілком: той
 * самий клас, що й `V-01`. Тепер вона тримається, доки людина її не розв'яже
 * (Retry або нова правка комірки). Поведінка конфлікту для людини — перелік
 * розбіжностей (`useCellPatch`, `BE-06`) — не змінена.
 *
 * ⚠ Комірку названо — тримається рівно вона (`cellsOfSaveError`). Не названо
 * жодної з надісланих — тримається ВЕСЬ пакет: інакше він пішов би знову
 * цілим і знову впав, тобто рівно та блокада, яку це виправляє. Так кожна
 * відмова позначає бодай одну правку, і повтор решти (`scheduleAutosave` у
 * викликача) гарантовано сходиться.
 *
 * ⚠ `ECR-CALC-0437` — відмова РЯДКА (бракує іншої, обов'язкової комірки), тож
 * тримаються правки названих рядків, і відпускає їх будь-яка нова правка того
 * самого рядка (`scope: 'row'`, `putPendingEdit`).
 */
export function rejectionMarksOf(
  error: unknown,
  attempted: readonly PendingEdit[],
): readonly { edit: PendingEdit; message: string; scope: 'cell' | 'row' }[] {
  if (!(error instanceof EcrApiError)) return [];
  // ⛔ AN-123 (`R1-03`/`R2-01`): `409 ECR-DOC-4091 lockTimeout` — минуще, як `5xx`:
  // до N-3 те саме очікування блокування давало `500`, і правки лишались
  // придатними до надсилання. Утримати їх — означало б, що після зняття
  // блокування нічого не довезеться само. Повтор із відступом — `holdRejectedEdits`.
  if (error.isTransientBusy) return [];
  if (![400, 403, 409, 422].includes(error.problem.status) && !error.isRequiredInputMissing) return [];

  if (error.isConflict) {
    // ⚠ Названі розбіжні комірки; не названо жодної з надісланих — увесь пакет
    // (конфлікт версії стосується рядка, і без переліку не відомо, чиєї комірки).
    const conflicts = error.conflicts as { rowKey?: unknown; columnCode?: unknown }[];
    const conflicted = new Set(
      conflicts.map((conflict) => `${String(conflict.rowKey)}:${String(conflict.columnCode)}`),
    );

    // ⛔ AN-104 (`D1-01`): `*` — розбіжність ЦІЛОГО рядка (комірки сервер не
    // назвав). Тримаються правки рівно цього рядка, а не весь пакет: правки
    // інших рядків конфлікт не зачепив, і утримувати їх означало б зупинити
    // збереження, якому ніщо не заважає.
    const wholeRows = new Set(
      conflicts
        .filter((conflict) => conflict.columnCode === '*')
        .map((conflict) => String(conflict.rowKey)),
    );
    const hit = attempted.filter(
      (edit) => conflicted.has(`${edit.rowKey}:${edit.columnCode}`) || wholeRows.has(edit.rowKey),
    );

    return (hit.length > 0 ? hit : attempted).map((edit) => ({
      edit,
      message: refusalText(error),
      scope: 'cell' as const,
    }));
  }

  if (error.isRequiredInputMissing) {
    const rows = new Set(error.requiredInputCells.map((cell) => cell.rowKey));
    const inRows = attempted.filter((edit) => rows.has(edit.rowKey));

    return (inRows.length > 0 ? inRows : attempted).map((edit) => ({
      edit,
      message: refusalText(error),
      scope: 'row' as const,
    }));
  }

  const named = new Map(
    cellsOfSaveError(error, attempted).map((cell) => [`${cell.rowKey}:${cell.columnCode}`, cell.message]),
  );
  const hit = attempted.filter((edit) => named.has(`${edit.rowKey}:${edit.columnCode}`));

  return (hit.length > 0 ? hit : attempted).map((edit) => ({
    edit,
    message: named.get(`${edit.rowKey}:${edit.columnCode}`) ?? refusalText(error),
    scope: 'cell' as const,
  }));
}

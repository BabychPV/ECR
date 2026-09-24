import { EcrApiError, type RequiredInputCell } from '@/api/client';
import type { PendingEdit } from './useCellPatch';

/**
 * Комірки, яких стосується конкретна відмова збереження (Q-30x, High).
 *
 * ⛔ До цього виправлення справжня причина відмови (наприклад, `Колонка «C1»
 * очікує число.` — `ECR-CELL-0422`) доїжджала до клієнта коректно, але ніде
 * не показувалась: тулбар малював лише заглушку «NOT SAVED — SEE THE ERROR
 * ABOVE», а сам текст губився в необробленому знеструмленні проміса
 * (`Uncaught (in promise)`), яке бачить лише консоль розробника, а не
 * оператор. `DocumentGrid.save` показує `error.message` в `Alert` — ця
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
      message: error.message,
    }));
}

/**
 * Які правки пакета ТРИМАТИ після відмови (`V-01`) — і чи тримати взагалі.
 *
 * ⛔ Тримаються лише відмови, які повторення НЕ вилікує: невірне значення
 * (`422`), заборона (`403`), кривий запит (`400`) і конфлікт версії рядка
 * (`409`). Мережа, `5xx`, `429` і `401` — минущі: наступний пакет має везти ті
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
  if (![400, 403, 409, 422].includes(error.problem.status) && !error.isRequiredInputMissing) return [];

  if (error.isConflict) {
    // ⚠ Названі розбіжні комірки; не названо жодної з надісланих — увесь пакет
    // (конфлікт версії стосується рядка, і без переліку не відомо, чиєї комірки).
    const conflicted = new Set(
      (error.conflicts as { rowKey?: unknown; columnCode?: unknown }[]).map(
        (conflict) => `${String(conflict.rowKey)}:${String(conflict.columnCode)}`,
      ),
    );
    const hit = attempted.filter((edit) => conflicted.has(`${edit.rowKey}:${edit.columnCode}`));

    return (hit.length > 0 ? hit : attempted).map((edit) => ({
      edit,
      message: error.message,
      scope: 'cell' as const,
    }));
  }

  if (error.isRequiredInputMissing) {
    const rows = new Set(error.requiredInputCells.map((cell) => cell.rowKey));
    const inRows = attempted.filter((edit) => rows.has(edit.rowKey));

    return (inRows.length > 0 ? inRows : attempted).map((edit) => ({
      edit,
      message: error.message,
      scope: 'row' as const,
    }));
  }

  const named = new Map(
    cellsOfSaveError(error, attempted).map((cell) => [`${cell.rowKey}:${cell.columnCode}`, cell.message]),
  );
  const hit = attempted.filter((edit) => named.has(`${edit.rowKey}:${edit.columnCode}`));

  return (hit.length > 0 ? hit : attempted).map((edit) => ({
    edit,
    message: named.get(`${edit.rowKey}:${edit.columnCode}`) ?? error.message,
    scope: 'cell' as const,
  }));
}

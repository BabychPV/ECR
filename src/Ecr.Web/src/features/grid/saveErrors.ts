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

/**
 * Комірки `AllowWithConfirmation`, правку яких людина ПІДТВЕРДИЛА (`ФВ-2.16`).
 *
 * ⛔ Сервер відхиляє батч правки людини з такою коміркою без прапорця
 * `confirmed` (`PatchCellsHandler.EnsureConfirmed`, `ECR-ACCS-0403`
 * `ConfirmationRequired`). Надсилають правки кілька шляхів — сітка (`save`),
 * документ за розмонтовану сітку (`saveOrphanSlice`), останній шанс перед
 * закриттям вкладки (`sendPatchBeacon`), — і всі збирають запит із СХОВИЩА
 * правок, у якому про підтвердження нічого немає. Тому факт підтвердження
 * лежить тут, а прапорець ставить сам `buildRequest`: жоден із шляхів не може
 * його забути.
 *
 * ⚠ Прапорець НЕ ставиться «завжди»: тоді серверна перевірка не ловила б
 * нічого — ні шлях клієнта, який обійшов діалог, ні правку, що стала вимагати
 * підтвердження вже після того, як її зробили (правило змінилося). Сюди
 * потрапляє лише те, що пройшло діалог.
 *
 * ⚠ Позначка живе до закриття документа (`resetConfirmed` у
 * `useDocumentPending`): повтор тієї самої правки (відхилена й виправлена,
 * undo/redo) — це те саме рішення людини щодо тієї самої комірки.
 */

/** Позначені комірки за зрізом `tableInstanceId:periodKey`. */
const confirmed = new Map<string, Set<string>>();

interface CellRef {
  rowKey: string;
  columnCode: string;
}

function sliceKeyOf(tableInstanceId: number, periodKey: number): string {
  return `${tableInstanceId}:${periodKey}`;
}

function cellKeyOf(cell: CellRef): string {
  return `${cell.rowKey}:${cell.columnCode}`;
}

/** Людина підтвердила правку цих комірок. */
export function markConfirmed(
  tableInstanceId: number,
  periodKey: number,
  cells: Iterable<CellRef>,
): void {
  const key = sliceKeyOf(tableInstanceId, periodKey);
  const set = confirmed.get(key) ?? new Set<string>();

  for (const cell of cells) set.add(cellKeyOf(cell));

  if (set.size > 0) confirmed.set(key, set);
}

/** Чи є серед правок хоч одна підтверджена — тоді батч їде з `confirmed`. */
export function hasConfirmed(
  tableInstanceId: number,
  periodKey: number,
  cells: Iterable<CellRef>,
): boolean {
  const set = confirmed.get(sliceKeyOf(tableInstanceId, periodKey));
  if (set === undefined) return false;

  for (const cell of cells) {
    if (set.has(cellKeyOf(cell))) return true;
  }

  return false;
}

/** Документ закрито — підтвердження більше нічого не значать. */
export function resetConfirmed(): void {
  confirmed.clear();
}

import { navigateToCell } from './cellNavigationPort';
import { focusedCell, lastFocusedSlice } from './focusStore';
import { key, ordered, allNeedsInput, inMonth, type NeedsInputTable } from './needsInputState';
import { isTypingOrDialogTarget, needsInputStepKey } from './shortcutKey';
import { showDone } from '@/shared/ui/notify';
import { t } from '@/shared/i18n';

// Стан і підписки живуть у `needsInputState` (лёгкий для маршруту документа); тут — той самий API + обхід.
export * from './needsInputState';

/**
 * Позиція курсора в обході: таблиця, у якій курсор ставили останнім, і його комірка; `null` — курсора ніде немає.
 * `from` — таблиця, з тулбару якої натиснули ‹ › (без курсора в ній — обхід від її початку).
 */
function cursor(from?: { readonly tableInstanceId: number; readonly periodKey: number }): {
  readonly table: NeedsInputTable;
  readonly row: number;
  readonly column: number;
} | null {
  const last = lastFocusedSlice();
  const candidates = ordered();
  const table =
    from !== undefined
      ? candidates.find((entry) => entry.tableInstanceId === from.tableInstanceId && entry.periodKey === from.periodKey)
      : candidates.find((entry) => key(entry.tableInstanceId, entry.periodKey) === last);
  if (table === undefined) return null;
  const focused = focusedCell(table.tableInstanceId, table.periodKey);
  if (focused === null) return { table, row: -1, column: Number.MAX_SAFE_INTEGER };

  return { table, row: focused.rowIndex, column: focused.columnIndex - table.labelOffset };
}

/**
 * Перейти до наступної (`1`) / попередньої (`-1`) порожньої обов'язкової комірки аркуша. Після останньої в таблиці —
 * перша наступної таблиці; після останньої на аркуші — нічого (`false`, той, хто кликав, каже «більше немає»).
 */
export function stepNeedsInput(
  step: 1 | -1,
  from?: { readonly tableInstanceId: number; readonly periodKey: number },
): boolean {
  const all = allNeedsInput();
  if (all.length === 0) return false;

  const at = cursor(from);
  const orderOf = (table: NeedsInputTable): number => ordered().indexOf(table);
  const compare = (entry: (typeof all)[number]): number => {
    if (at === null) return step > 0 ? 1 : -1;
    const tableDelta = orderOf(entry.table) - orderOf(at.table);
    if (tableDelta !== 0) return tableDelta;
    return entry.cell.rowIndex - at.row || entry.cell.columnIndex - at.column;
  };

  const target =
    step > 0 ? all.find((entry) => compare(entry) > 0) : [...all].reverse().find((entry) => compare(entry) < 0);
  if (target === undefined) return false;

  navigateToCell({
    tableDefId: target.table.tableDefId,
    tableInstanceId: target.table.tableInstanceId,
    rowKey: target.cell.rowKey,
    columnCode: target.cell.columnCode,
  });

  return true;
}

/** Перша порожня обов'язкова комірка аркуша (Submit веде сюди); `false` — таких немає. */
export function goToFirstNeedsInput(month: number | null = null): boolean {
  const first = allNeedsInput().find((entry) => inMonth(entry, month));
  if (first === undefined) return false;

  navigateToCell({
    tableDefId: first.table.tableDefId,
    tableInstanceId: first.table.tableInstanceId,
    rowKey: first.cell.rowKey,
    columnCode: first.cell.columnCode,
  });

  return true;
}

/**
 * F8 / Shift+F8 — ОДИН обробник на сторінку, хоч сіток на аркуші десятки: ставить перша сітка, знімає остання.
 * ⚠ Не з поля вводу, редактора комірки чи діалогу (`isTypingOrDialogTarget`): набір у редакторі ще не зафіксовано.
 */
let keyUsers = 0;

function onKey(event: KeyboardEvent): void {
  const step = needsInputStepKey(event);
  if (step === null || isTypingOrDialogTarget(event.target)) return;
  if (allNeedsInput().length === 0) return;

  event.preventDefault();
  if (!stepNeedsInput(step)) showDone(t('grid.needsInput.none'));
}

export function installNeedsInputKeys(): () => void {
  keyUsers += 1;
  if (keyUsers === 1) window.addEventListener('keydown', onKey);

  return () => {
    keyUsers -= 1;
    if (keyUsers === 0) window.removeEventListener('keydown', onKey);
  };
}

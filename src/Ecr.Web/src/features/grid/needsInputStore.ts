import { useSyncExternalStore } from 'react';
import { requestCellNavigation } from './cellNavigation';
import { focusedCell, lastFocusedSlice } from './focusStore';
import type { NeedsInputCell } from './needsInput';
import { isTypingOrDialogTarget, needsInputStepKey } from './shortcutKey';
import { showDone } from '@/shared/ui/notify';
import { t } from '@/shared/i18n';

/**
 * ✎ DOCUX L2: порожні обов'язкові комірки ВСІХ змонтованих сіток аркуша — одне сховище.
 *
 * Навіщо модульне, а не стан сторінки: сітки живуть у лінивому чанку (`SheetTables`, `D-132`), а лічильник на кнопці
 * «Submit» (L9), F8 / Shift+F8 і ‹ › у тулбарі кожної таблиці мусять бачити одне й те саме. Кожна сітка публікує свої
 * комірки (`publishNeedsInput`) і знімає їх при розмонтуванні.
 *
 * ⚠ Лише ЗМОНТОВАНІ сітки: таблиця далеко внизу аркуша монтується за прокруткою, і її порожніх комірок клієнт ще не
 * знає. Остаточне слово при поданні — за сервером (L6).
 */
export interface NeedsInputTable {
  readonly tableInstanceId: number;
  readonly tableDefId: number;
  readonly periodKey: number;
  /** Порядок таблиці на аркуші (обхід F8 переходить до наступної таблиці). */
  readonly order: number;
  /** Зсув відрендереної колонки: 1, коли сітка має колонку підпису рядка. */
  readonly labelOffset: number;
  readonly cells: readonly NeedsInputCell[];
}

const tables = new Map<string, NeedsInputTable>();
/** ✎ DOCUX L9: після спроби подати — рамка 2 px на всіх порожніх обов'язкових комірках (поки їх не заповнять). */
let emphasized = false;
const listeners = new Set<() => void>();
let version = 0;

function key(tableInstanceId: number, periodKey: number): string {
  return `${String(tableInstanceId)}:${String(periodKey)}`;
}

function notify(): void {
  version += 1;
  for (const listener of listeners) listener();
}

export function publishNeedsInput(table: NeedsInputTable): void {
  const id = key(table.tableInstanceId, table.periodKey);
  const known = tables.get(id);
  if (known !== undefined && sameCells(known, table)) return;
  tables.set(id, table);
  notify();
}

export function clearNeedsInput(tableInstanceId: number, periodKey: number): void {
  if (tables.delete(key(tableInstanceId, periodKey))) notify();
}

/** Для тестів: забути все. */
export function resetNeedsInput(): void {
  tables.clear();
  emphasized = false;
  notify();
}

/** Підсилити позначки (Submit спробували з порожніми обов'язковими); знімається, щойно їх не лишилось. */
export function emphasizeNeedsInput(on: boolean): void {
  if (emphasized === on) return;
  emphasized = on;
  notify();
}

function isEmphasized(): boolean {
  return emphasized && allNeedsInput().length > 0;
}

/** Чи позначки підсилено (рамка 2 px), з підпискою. */
export function useNeedsInputEmphasis(): boolean {
  return useSyncExternalStore(subscribe, isEmphasized, isEmphasized);
}

function sameCells(a: NeedsInputTable, b: NeedsInputTable): boolean {
  if (a.order !== b.order || a.labelOffset !== b.labelOffset || a.tableDefId !== b.tableDefId) return false;
  if (a.cells.length !== b.cells.length) return false;

  return a.cells.every((cell, index) => {
    const other = b.cells[index];
    return other !== undefined && cell.rowKey === other.rowKey && cell.columnCode === other.columnCode && cell.level === other.level;
  });
}

function ordered(): NeedsInputTable[] {
  return [...tables.values()].sort((a, b) => a.order - b.order || a.tableInstanceId - b.tableInstanceId);
}

/** Плаский перелік у порядку обходу: таблиця за таблицею, рядок за рядком. */
export function allNeedsInput(): { readonly table: NeedsInputTable; readonly cell: NeedsInputCell }[] {
  return ordered().flatMap((table) => table.cells.map((cell) => ({ table, cell })));
}

export interface NeedsInputSummary {
  readonly count: number;
  readonly blocking: number;
}

let cachedVersion = -1;
let cachedSummary: NeedsInputSummary = { count: 0, blocking: 0 };

function summary(): NeedsInputSummary {
  if (cachedVersion !== version) {
    const all = allNeedsInput();
    cachedSummary = { count: all.length, blocking: all.filter((entry) => entry.cell.level === 'block').length };
    cachedVersion = version;
  }

  return cachedSummary;
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

/** Скільки порожніх обов'язкових комірок на аркуші (змонтовані сітки), з підпискою. */
export function useNeedsInputSummary(): NeedsInputSummary {
  return useSyncExternalStore(subscribe, summary, summary);
}

/** Поточне зведення без підписки (обробник кліку «Submit»). */
export function needsInputSummary(): NeedsInputSummary {
  return summary();
}

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

  requestCellNavigation({
    tableDefId: target.table.tableDefId,
    tableInstanceId: target.table.tableInstanceId,
    rowKey: target.cell.rowKey,
    columnCode: target.cell.columnCode,
  });

  return true;
}

/** Перша порожня обов'язкова комірка аркуша (Submit веде сюди); `false` — таких немає. */
export function goToFirstNeedsInput(): boolean {
  const first = allNeedsInput()[0];
  if (first === undefined) return false;

  requestCellNavigation({
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

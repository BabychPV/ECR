import { useSyncExternalStore } from 'react';
import type { NeedsInputCell } from './needsInput';

/**
 * (b18 D-132: лише стан і підписки — обхід F8/кроки в `needsInputStore`, щоб SheetStrip не тягнув їх у маршрут.)
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

export function key(tableInstanceId: number, periodKey: number): string {
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
  unmountedTables = 0;
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
    return other !== undefined && cell.rowKey === other.rowKey && cell.columnCode === other.columnCode && cell.level === other.level && (cell.ownerPeriodKey ?? null) === (other.ownerPeriodKey ?? null);
  });
}

export function ordered(): NeedsInputTable[] {
  return [...tables.values()].sort((a, b) => a.order - b.order || a.tableInstanceId - b.tableInstanceId);
}

/** Плаский перелік у порядку обходу: таблиця за таблицею, рядок за рядком. */
export function allNeedsInput(): { readonly table: NeedsInputTable; readonly cell: NeedsInputCell }[] {
  return ordered().flatMap((table) => table.cells.map((cell) => ({ table, cell })));
}

export interface NeedsInputSummary {
  readonly count: number;
  readonly blocking: number;
  /** Скільки таблиць аркуша ще не змонтовано (їхніх порожніх комірок клієнт не знає — перевіряє сервер). */
  readonly unmountedTables: number;
}

/**
 * ✎ DOCUX L9: річна розкладка — подається МІСЯЦЬ: рахуються комірки його колонки і колонок без місяця (спільних для
 * року); `null` — без фільтра (звичайний документ).
 */
export function inMonth(entry: { readonly cell: NeedsInputCell }, month: number | null): boolean {
  const owner = entry.cell.ownerPeriodKey ?? null;
  return month === null || owner === null || owner === month;
}

let unmountedTables = 0;
let cachedKey = '';
let cachedSummary: NeedsInputSummary = { count: 0, blocking: 0, unmountedTables: 0 };

function summaryFor(month: number | null): NeedsInputSummary {
  const key = `${String(version)}:${String(month)}`;
  if (cachedKey !== key) {
    const all = allNeedsInput().filter((entry) => inMonth(entry, month));
    cachedSummary = { count: all.length, blocking: all.filter((entry) => entry.cell.level === 'block').length, unmountedTables };
    cachedKey = key;
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
export function useNeedsInputSummary(month: number | null = null): NeedsInputSummary {
  const read = (): NeedsInputSummary => summaryFor(month);
  return useSyncExternalStore(subscribe, read, read);
}

/** ✎ DOCUX L9: скільки таблиць аркуша ще не змонтовано (лінива прокрутка) — `SheetTables`. */
export function publishUnmountedTables(count: number): void {
  if (unmountedTables === count) return;
  unmountedTables = count;
  notify();
}

/** Поточне зведення без підписки (обробник кліку «Submit»). */
export function needsInputSummary(month: number | null = null): NeedsInputSummary {
  return summaryFor(month);
}


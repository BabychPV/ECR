import { useSyncExternalStore } from 'react';

/**
 * ✎ UAT 08.11: сховище «рядки, що потребують уваги» — без жодної залежності від сітки, щоб рядок стану
 * збереження й інспектор (чанк сторінки) не тягнули код сітки (`D-132`). Наповнює його `DocumentGrid`
 * (`rowAttention.ts`).
 */

/** Рядок, що потребує уваги (зараз — лише втрачене посилання на довідник). */
export interface RowAttentionItem {
  /** `"{tableInstanceId}:{rowKey}"` — адреса пункту в Issues. */
  readonly key: string;
  readonly tableInstanceId: number;
  readonly periodKey: number;
  readonly rowKey: string;
  readonly rowLabel: string;
  /** Пояснення простими словами (без «окремого погодження»). */
  readonly reason: string;
}

/** Що відомо про одну таблицю. */
export interface TableAttention {
  readonly tableInstanceId: number;
  readonly periodKey: number;
  readonly rows: readonly RowAttentionItem[];
  /** Закриті колонки (відмова `PeriodClosed`) — «January–August»; `null` — немає. */
  readonly lockedMonths: string | null;
}

const tables = new Map<number, TableAttention>();
let snapshot: readonly TableAttention[] = [];
let focus: { readonly key: string; readonly seq: number } | null = null;
const listeners = new Set<() => void>();

function emit(): void {
  snapshot = [...tables.values()];
  for (const listener of listeners) listener();
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener);

  return () => {
    listeners.delete(listener);
  };
}

/** Сітка таблиці кладе свій стан; `null` — прибирає (розмонтування). Той самий об'єкт — без сповіщення. */
export function publishTableAttention(tableInstanceId: number, value: TableAttention | null): void {
  const current = tables.get(tableInstanceId);
  if (value === null) {
    if (current === undefined) return;
    tables.delete(tableInstanceId);
  } else {
    if (current === value) return;
    tables.set(tableInstanceId, value);
  }
  emit();
}

/** Знак «!» біля підпису рядка: просить Issues відкритися на цьому пункті. */
export function requestAttentionFocus(key: string): void {
  focus = { key, seq: (focus?.seq ?? 0) + 1 };
  emit();
}

/** Таблиці періоду з чимось, що потребує уваги (рядки або закриті місяці). */
export function attentionForPeriod(all: readonly TableAttention[], periodKey: number): TableAttention[] {
  return all.filter((table) => table.periodKey === periodKey && (table.rows.length > 0 || table.lockedMonths !== null));
}

/** Скільки рядків періоду потребують уваги. */
export function attentionRowCount(all: readonly TableAttention[], periodKey: number): number {
  return attentionForPeriod(all, periodKey).reduce((sum, table) => sum + table.rows.length, 0);
}

export function useTableAttention(): readonly TableAttention[] {
  return useSyncExternalStore(subscribe, () => snapshot, () => snapshot);
}

export function useAttentionFocus(): { readonly key: string; readonly seq: number } | null {
  return useSyncExternalStore(subscribe, () => focus, () => focus);
}

/** Лише для тестів: чисте сховище між тестами. */
export function resetRowAttentionForTests(): void {
  tables.clear();
  focus = null;
  emit();
}

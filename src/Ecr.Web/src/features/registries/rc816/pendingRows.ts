import { normalizeUserDecimal } from '@/shared/format/userDecimal';
import type { RegistryBatchItem, RegistryBatchResult, RegistryRow } from '../rows/api';

/**
 * Незбережені зміни однієї панелі master-detail (`ФВ-8.16`): рядки з сервера плюс правки,
 * нові й позначені до видалення рядки. Зберігаються ОДНИМ пакетом (`POST …/entries/batch`) —
 * так агрегатне правило «Σ складу = 100» перевіряє стан після всього пакета, а не після кожного
 * рядка (FEATURE-REGISTRY-TABLES §6, «агрегатні правила й поштучний ввід»).
 */
export interface PendingRow {
  /** Стабільний ключ рядка: `e<id>` для наявного, `clientRowId` для нового. */
  readonly key: string;
  /** Запис; `null` — новий. */
  readonly id: number | null;
  /** Код запису; для нового — введений людиною (порожньо — `CodeMode = Auto`). */
  readonly code: string;
  /** Назва з сервера. */
  readonly display: string;
  /** Жетон конкуренції з `GET …/rows`; повертається як `baseVersion`. */
  readonly version: string | null;
  /** Поточні значення за кодами полів (рядком, як їх віддає сервер). */
  readonly values: Readonly<Record<string, string>>;
  /** Значення з сервера — з ними порівнюється правка. */
  readonly original: Readonly<Record<string, string>>;
  /** Підписи значень з сервера (`Lookup` — назва цілі). */
  readonly displays: Readonly<Record<string, string>>;
  /** Позначений до видалення (наявний рядок; новий просто прибирається). */
  readonly deleted: boolean;
}

/** Рядок з сервера → рядок панелі без змін. */
export function fromServer(row: RegistryRow): PendingRow {
  const values: Record<string, string> = {};
  const displays: Record<string, string> = {};

  for (const [field, value] of Object.entries(row.values)) {
    values[field] = value.value ?? '';
    if (value.display !== null) displays[field] = value.display;
  }

  return {
    key: `e${row.id}`,
    id: row.id,
    code: row.code,
    display: row.display,
    version: row.version,
    values,
    original: values,
    displays,
    deleted: false,
  };
}

/**
 * Новий рядок-частина.
 *
 * ⛔ Поле композиції заповнюється САМЕ — батьком, обраним у панелі вище. Саме цього вимагає
 * `ФВ-8.16`: «редагуються як одна таблиця master-detail без введення ідентифікаторів руками».
 *
 * @param key Ключ рядка на клієнті (`clientRowId` пакета).
 * @param composition Поле композиції і батько; `null` — рядок верхньої панелі.
 */
export function newRow(key: string, composition: { field: string; parentId: number } | null): PendingRow {
  const values = composition === null ? {} : { [composition.field]: String(composition.parentId) };

  return {
    key,
    id: null,
    code: '',
    display: '',
    version: null,
    values,
    original: {},
    displays: {},
    deleted: false,
  };
}

/** Змінює значення поля рядка. */
export function setValue(rows: readonly PendingRow[], key: string, field: string, value: string): PendingRow[] {
  return rows.map((row) => (row.key === key ? { ...row, values: { ...row.values, [field]: value } } : row));
}

/** Змінює код нового рядка (у наявного код не змінюється). */
export function setCode(rows: readonly PendingRow[], key: string, code: string): PendingRow[] {
  return rows.map((row) => (row.key === key && row.id === null ? { ...row, code } : row));
}

/** Позначає рядок до видалення або знімає позначку; новий рядок прибирається зовсім. */
export function toggleDelete(rows: readonly PendingRow[], key: string): PendingRow[] {
  return rows
    .filter((row) => !(row.key === key && row.id === null))
    .map((row) => (row.key === key ? { ...row, deleted: !row.deleted } : row));
}

/** Поля, значення яких змінено відносно сервера. */
export function changedFields(row: PendingRow): string[] {
  const fields = new Set([...Object.keys(row.values), ...Object.keys(row.original)]);
  return [...fields].filter((field) => (row.values[field] ?? '') !== (row.original[field] ?? ''));
}

/** Чи має рядок незбережену зміну. */
export function isDirty(row: PendingRow): boolean {
  return row.id === null || row.deleted || changedFields(row).length > 0;
}

/** Скільки рядків панелі змінено. */
export function dirtyCount(rows: readonly PendingRow[]): number {
  return rows.filter(isDirty).length;
}

/** Значення числового поля → інваріантний запис (`12,5` → `12.5`); неоднозначне й не число — як є. */
function sent(field: string, value: string, numeric: ReadonlySet<string>): string {
  if (!numeric.has(field)) return value;
  const read = normalizeUserDecimal(value);
  return read.kind === 'number' ? read.text : value;
}

/**
 * Пакет змін панелі.
 *
 * ⚠ Наявний рядок надсилає лише змінені поля («поле, якого немає, не змінюється»), а очищене поле —
 * як `null`: порожній рядок сервер прочитав би як значення. Новий рядок порожніх полів не надсилає.
 * `baseVersion` іде з кожною правкою й видаленням: чужу зміну після відкриття сервер назве
 * `entryChanged`, а не перезапише мовчки.
 *
 * ⚠ Числа (`numeric` — коди полів `Int`/`Decimal`) ідуть інваріантним записом за правилами
 * сервера (`normalizeUserDecimal`): ті самі, що в сітці даних довідника (rc812) і в Σ.
 * Неоднозначне `1,234` іде як є — сервер назве його `valueAmbiguousSeparator` у рядку.
 */
export function batchItems(rows: readonly PendingRow[], numeric: ReadonlySet<string> = new Set()): RegistryBatchItem[] {
  const items: RegistryBatchItem[] = [];

  for (const row of rows) {
    if (row.id !== null && row.deleted) {
      items.push({ clientRowId: row.key, op: 'delete', id: row.id, code: null, baseVersion: row.version, values: null });
      continue;
    }

    if (row.id === null) {
      const values: Record<string, string> = {};
      for (const [field, value] of Object.entries(row.values)) {
        if (value.trim() !== '') values[field] = sent(field, value.trim(), numeric);
      }
      items.push({
        clientRowId: row.key,
        op: 'upsert',
        id: null,
        code: row.code.trim() === '' ? null : row.code.trim(),
        baseVersion: null,
        values,
      });
      continue;
    }

    const changed = changedFields(row);
    if (changed.length === 0) continue;

    const values: Record<string, string | null> = {};
    for (const field of changed) {
      const value = (row.values[field] ?? '').trim();
      values[field] = value === '' ? null : sent(field, value, numeric);
    }
    items.push({ clientRowId: row.key, op: 'upsert', id: row.id, code: null, baseVersion: row.version, values });
  }

  return items;
}

/** Помилка рядка пакета, готова до показу. */
export interface RowProblem {
  /** Поле; `null` — рядок цілком. */
  readonly field: string | null;
  readonly messageKey: string;
  readonly params: Readonly<Record<string, string>>;
}

/** Помилки звіту пакета за ключем рядка. */
export function problemsByRow(result: RegistryBatchResult | null): Map<string, RowProblem[]> {
  const map = new Map<string, RowProblem[]>();
  for (const row of result?.rows ?? []) {
    if (row.errors.length === 0) continue;
    map.set(
      row.clientRowId,
      row.errors.map((error) => ({ field: error.field, messageKey: error.messageKey, params: error.params })),
    );
  }
  return map;
}

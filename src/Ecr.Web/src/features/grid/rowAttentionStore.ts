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
  /**
   * ✎ F13-p210: `Warning` — попередження перевірки (дозвіл не покриває місяці лише з обчисленими значеннями):
   * не помилка, нічого не блокує; немає — рядок з утраченим посиланням (помилка).
   */
  readonly severity?: 'Warning';
  /** Куди веде «!»: для попередження — пункт перевірки в Issues (`data-inspector-finding`); немає — `key`. */
  readonly focusKey?: string;
}

/** Що відомо про одну таблицю. */
export interface TableAttention {
  readonly tableInstanceId: number;
  /** Шаблонна таблиця (адреса знахідок перевірки); немає — невідома. */
  readonly tableDefId?: number;
  readonly periodKey: number;
  readonly rows: readonly RowAttentionItem[];
  /** Закриті колонки (відмова `PeriodClosed`) — «January–August»; `null` — немає. */
  readonly lockedMonths: string | null;
  /**
   * ✎ 12.11 (людина, doc 96 T3_00): закриті місяці — по рядку, а не одним реченням на таблицю з сирими заголовками
   * колонок. Немає — старий видавець (лише `lockedMonths`), Issues показує речення таблиці.
   */
  readonly lockedRows?: readonly LockedRowItem[];
}

/** Рядок, у якому є комірки закритих місяців (відмова `PeriodClosed`). */
export interface LockedRowItem {
  /** `"{tableInstanceId}:{rowKey}:locked"` — адреса пункту в Issues. */
  readonly key: string;
  readonly tableInstanceId: number;
  readonly periodKey: number;
  readonly rowKey: string;
  /** Назва рядка (`label`); `null` — немає або збігається з кодом. */
  readonly rowName: string | null;
  /** Місяці закритих колонок рядка, 1..12, без повторів і за порядком. */
  readonly months: readonly number[];
  /** Періоди цих місяців (`YYYYMM`), без повторів і за порядком, — для «Reopen … for this document…». */
  readonly periodKeys: readonly number[];
  /** Закриті колонки рядка назвами заголовків — лише в розгортанні. */
  readonly columns: string;
  /** Перша закрита колонка — куди веде клік (як у пункті перевірки). */
  readonly columnCode: string;
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
  return all.filter(
    (table) =>
      table.periodKey === periodKey &&
      (table.rows.length > 0 || table.lockedMonths !== null || (table.lockedRows?.length ?? 0) > 0),
  );
}

/** Скільки рядків періоду потребують уваги. */
export function attentionRowCount(all: readonly TableAttention[], periodKey: number): number {
  return attentionForPeriod(all, periodKey).reduce((sum, table) => sum + table.rows.length, 0);
}

export function useTableAttention(): readonly TableAttention[] {
  return useSyncExternalStore(subscribe, () => snapshot, () => snapshot);
}

/**
 * ✎ F13-p210: знахідка перевірки (Issues), яку сітка показує на самому рядку. Джерело — результат перевірки, що
 * його вже читає сторінка документа (`ValidationFindingDto`); нового запиту немає.
 */
export interface RowFinding {
  readonly tableDefId: number;
  readonly rowKey: string;
  readonly columnCode: string | null;
  /** Текст сервера, уже мовою читача: дозвіл, чинність, місяці і що зробити. */
  readonly message: string;
}

export interface PeriodRowFindings {
  readonly periodKey: number;
  readonly findings: readonly RowFinding[];
}

/** Правило (h): дозвіл і місяці з даними (`PermitCoverageIssues.RowRuleCode`). */
export const PermitValidityRule = 'PERMIT-VALIDITY';

/** Знахідка в тій формі, як її віддає сервер (підмножина `ValidationFindingDto`). */
interface FindingLike {
  readonly tableDefId: number;
  readonly rowKey: string | null;
  readonly columnCode: string | null;
  readonly ruleCode: string;
  readonly severity: string;
  readonly message: string;
}

/** Попередження правила дозволу, прив'язані до рядка (помилки й знахідки без рядка — ні). */
export function permitRowWarningsOf(messages: readonly FindingLike[]): RowFinding[] {
  return messages.flatMap((finding) =>
    finding.ruleCode === PermitValidityRule && finding.severity === 'Warning' && finding.rowKey !== null && finding.tableDefId > 0
      ? [{ tableDefId: finding.tableDefId, rowKey: finding.rowKey, columnCode: finding.columnCode, message: finding.message }]
      : [],
  );
}

let rowFindings: PeriodRowFindings | null = null;

/** Сторінка документа кладе знахідки показаного результату перевірки; `null` — прибирає. */
export function publishRowFindings(value: PeriodRowFindings | null): void {
  if (rowFindings === value) return;
  if (
    value !== null &&
    rowFindings !== null &&
    rowFindings.periodKey === value.periodKey &&
    rowFindings.findings.length === value.findings.length &&
    rowFindings.findings.every((finding, index) => {
      const next = value.findings[index];
      return (
        next !== undefined &&
        next.tableDefId === finding.tableDefId &&
        next.rowKey === finding.rowKey &&
        next.columnCode === finding.columnCode &&
        next.message === finding.message
      );
    })
  ) {
    return;
  }
  rowFindings = value;
  emit();
}

export function useRowFindings(): PeriodRowFindings | null {
  return useSyncExternalStore(subscribe, () => rowFindings, () => rowFindings);
}

/** Підписи рядків, відомі сіткам (`"{tableDefId}:{rowKey}"` → назва), — для адреси знахідки в Issues. */
export function rowLabelsOf(all: readonly TableAttention[]): ReadonlyMap<string, string> {
  const labels = new Map<string, string>();
  for (const table of all) {
    if (table.tableDefId === undefined) continue;
    for (const row of table.rows) labels.set(`${String(table.tableDefId)}:${row.rowKey}`, row.rowLabel);
  }

  return labels;
}

export function useAttentionFocus(): { readonly key: string; readonly seq: number } | null {
  return useSyncExternalStore(subscribe, () => focus, () => focus);
}

/** Лише для тестів: чисте сховище між тестами. */
export function resetRowAttentionForTests(): void {
  tables.clear();
  focus = null;
  rowFindings = null;
  emit();
}

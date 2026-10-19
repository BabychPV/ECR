import type { PermitColumn, PermitRow } from './columns';

/**
 * Групи стовпців широкої таблиці (PM-06). Дзеркало `DefTable` мокапу
 * (`docs/design/permits/screens-permits.js`): «так 40 стовпців не лякають».
 *
 * Правило за замовчуванням: коли стовпців БІЛЬШЕ за {@link WideThreshold}, групи з
 * {@link CollapseMinColumns} і більше стовпцями згорнуті. Згорнута група — одна підсумкова
 * колонка: Σ, якщо ВСІ її стовпці числові, інакше «…» (щоб не підсумовувати непідсумовуване).
 */

/** Таблиця «широка», якщо в ній строго більше стовпців. */
export const WideThreshold = 12;

/** Мінімальна кількість стовпців групи, щоб її згортати за замовчуванням. */
export const CollapseMinColumns = 5;

/** Режим перемикача «Grouped / All columns». */
export type ColumnMode = 'grouped' | 'all';

/** Один видимий елемент рядка таблиці: звичайний стовпець або підсумок згорнутої групи. */
export type VisibleItem =
  | { readonly kind: 'column'; readonly column: PermitColumn }
  | { readonly kind: 'summary'; readonly group: string; readonly columns: readonly PermitColumn[] };

/** Суміжний відрізок верхнього рядка шапки: група (або `null` — стовпці без групи). */
export interface GroupSpan {
  readonly group: string | null;
  readonly span: number;
  /** Індекс першого видимого елемента відрізка. */
  readonly start: number;
}

export function isWide(columns: readonly PermitColumn[]): boolean {
  return columns.length > WideThreshold;
}

/** Назви груп у порядку першої появи. */
export function groupNames(columns: readonly PermitColumn[]): string[] {
  const seen = new Set<string>();
  const out: string[] = [];
  for (const column of columns) {
    if (column.group !== undefined && column.group !== '' && !seen.has(column.group)) {
      seen.add(column.group);
      out.push(column.group);
    }
  }
  return out;
}

/** Скільки стовпців у кожній групі. */
export function groupSizes(columns: readonly PermitColumn[]): ReadonlyMap<string, number> {
  const sizes = new Map<string, number>();
  for (const column of columns) {
    if (column.group !== undefined && column.group !== '') {
      sizes.set(column.group, (sizes.get(column.group) ?? 0) + 1);
    }
  }
  return sizes;
}

/**
 * Які групи згорнуті спочатку: у широкій таблиці — групи з ≥ 5 стовпців, у вузькій — жодної
 * (її видно повністю, і ховати там нічого).
 */
export function defaultCollapsed(columns: readonly PermitColumn[]): Set<string> {
  return isWide(columns) ? collapsedForMode(columns, 'grouped') : new Set();
}

/**
 * Перемикач «Grouped / All columns»: `grouped` згортає групи з ≥ 5 стовпців, `all` розгортає все.
 * Дзеркало `setCollapsed(on)` мокапу — з тією різницею, що тут це чиста функція.
 */
export function collapsedForMode(columns: readonly PermitColumn[], mode: ColumnMode): Set<string> {
  if (mode === 'all') return new Set();
  const collapsed = new Set<string>();
  for (const [group, size] of groupSizes(columns)) {
    if (size >= CollapseMinColumns) collapsed.add(group);
  }
  return collapsed;
}

/** Згорнути/розгорнути одну групу (повертає новий набір). */
export function toggleGroup(collapsed: ReadonlySet<string>, group: string): Set<string> {
  const next = new Set(collapsed);
  if (next.has(group)) next.delete(group);
  else next.add(group);
  return next;
}

/**
 * Видимі елементи рядка. Згорнута група дає ОДИН елемент на місці свого ПЕРШОГО стовпця; решта
 * стовпців цієї групи, навіть якщо вони розкидані по таблиці, не виводяться окремо.
 */
export function visibleItems(
  columns: readonly PermitColumn[],
  collapsed: ReadonlySet<string>,
): VisibleItem[] {
  const items: VisibleItem[] = [];
  const emitted = new Set<string>();

  for (const column of columns) {
    const group = column.group;
    if (group !== undefined && group !== '' && collapsed.has(group)) {
      if (!emitted.has(group)) {
        emitted.add(group);
        items.push({ kind: 'summary', group, columns: columns.filter((c) => c.group === group) });
      }
    } else {
      items.push({ kind: 'column', column });
    }
  }

  return items;
}

function groupOf(item: VisibleItem): string | null {
  if (item.kind === 'summary') return item.group;
  const group = item.column.group;
  return group !== undefined && group !== '' ? group : null;
}

/** Верхній рядок шапки: суміжні елементи однієї групи зливаються в один відрізок. */
export function groupSpans(items: readonly VisibleItem[]): GroupSpan[] {
  const spans: GroupSpan[] = [];
  let i = 0;
  while (i < items.length) {
    const group = groupOf(items[i]!);
    if (group === null) {
      spans.push({ group: null, span: 1, start: i });
      i += 1;
      continue;
    }
    let j = i;
    while (j < items.length && groupOf(items[j]!) === group) j += 1;
    spans.push({ group, span: j - i, start: i });
    i = j;
  }
  return spans;
}

/** Сума для числової колонки; не-числа (рядки, `null`) не додаються. Зайві хвости float зрізані. */
export function sumColumn(rows: readonly PermitRow[], code: string): number {
  let total = 0;
  for (const row of rows) {
    const value = row[code];
    if (typeof value === 'number' && Number.isFinite(value)) total += value;
  }
  return roundSum(total);
}

/**
 * Σ згорнутої групи для одного рядка. `null`, якщо в групі є не-числові стовпці: сума «вибір +
 * число» нічого не означає, а «0» там була б вигадкою.
 */
export function summaryValue(row: PermitRow, columns: readonly PermitColumn[]): number | null {
  if (columns.length === 0 || !columns.every((column) => column.type === 'number')) return null;
  let total = 0;
  for (const column of columns) {
    const value = row[column.code];
    if (typeof value === 'number' && Number.isFinite(value)) total += value;
  }
  return roundSum(total);
}

/** `0.1 + 0.2` → `0.3`: підсумки показують людям, а не порівнюють бітово. */
function roundSum(value: number): number {
  return Number(value.toFixed(9));
}

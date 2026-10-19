import type { PermitTypeColumn, PermitTypeRule, PermitTypeTable } from '../api/types';
import type { TypeDraft } from './draftTypes';

/*
 * Порівняння робочої копії з тим, що збережено на сервері (Permits, PM-08b): звідси «N unsaved
 * changes» у шапці й «чи є що зберігати». Чисте, без React.
 */

function sortKeys(_key: string, value: unknown): unknown {
  if (value === null || typeof value !== 'object' || Array.isArray(value)) return value;
  const entries = Object.entries(value as Record<string, unknown>).sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0));
  return Object.fromEntries(entries);
}

function canon(value: unknown): string {
  return JSON.stringify(value, sortKeys);
}

/** Чи дві копії однакові за змістом (порядок ключів обʼєктів не важить, порядок масивів — важить). */
export function sameDraft(a: TypeDraft, b: TypeDraft): boolean {
  return canon(a) === canon(b);
}

function byCode<T extends { code: string }>(items: readonly T[]): Map<string, T> {
  return new Map(items.map((item) => [item.code.toLowerCase(), item]));
}

function countList<T extends { code: string }>(before: readonly T[], after: readonly T[]): number {
  const left = byCode(before);
  const right = byCode(after);
  let count = 0;
  for (const [code, item] of right) {
    const old = left.get(code);
    if (old === undefined || canon(old) !== canon(item)) count += 1;
  }
  for (const code of left.keys()) if (!right.has(code)) count += 1;
  return count;
}

function countColumns(before: readonly PermitTypeColumn[], after: readonly PermitTypeColumn[]): number {
  const stripOrder = (c: PermitTypeColumn): PermitTypeColumn => ({ ...c, order: 0 });
  let count = countList(before.map(stripOrder), after.map(stripOrder));
  const common = new Set(before.map((c) => c.code.toLowerCase()));
  const stillThere = new Set(after.map((c) => c.code.toLowerCase()));
  const keptBefore = before.map((c) => c.code.toLowerCase()).filter((code) => stillThere.has(code));
  const keptAfter = after.map((c) => c.code.toLowerCase()).filter((code) => common.has(code));
  if (keptBefore.join('|') !== keptAfter.join('|')) count += 1;
  return count;
}

function countTables(before: readonly PermitTypeTable[], after: readonly PermitTypeTable[]): number {
  const left = byCode(before);
  let count = 0;
  for (const table of after) {
    const old = left.get(table.code.toLowerCase());
    if (old === undefined) {
      count += 1;
      continue;
    }
    if (canon(old.nameL10n) !== canon(table.nameL10n)) count += 1;
    count += countColumns(old.columns, table.columns);
    count += countList<PermitTypeRule>(old.rules, table.rules);
  }
  const right = byCode(after);
  for (const code of left.keys()) if (!right.has(code)) count += 1;
  return count;
}

/**
 * Скільки окремих правок між збереженим і поточним: додана/видалена/змінена таблиця, стовпець чи
 * перевірка — по одній; зміна порядку стовпців таблиці — одна. Нуль — копії однакові.
 */
export function countChanges(saved: TypeDraft, current: TypeDraft): number {
  let count = 0;
  if (canon(saved.nameL10n) !== canon(current.nameL10n)) count += 1;
  if (saved.category !== current.category) count += 1;
  count += countList<PermitTypeRule>(saved.rules, current.rules);
  count += countTables(saved.tables, current.tables);
  return count;
}

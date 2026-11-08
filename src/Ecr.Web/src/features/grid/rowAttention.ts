import type { TableSliceDto } from '@/api/types';
import { t } from '@/shared/i18n';
import { cellKey } from './permissions';
import type { RowAttentionItem, TableAttention } from './rowAttentionStore';

export {
  publishTableAttention,
  requestAttentionFocus,
  type RowAttentionItem,
  type TableAttention,
} from './rowAttentionStore';

/**
 * ✎ UAT 08.11 (Water Report, рядок «Sea Water Intake»; людина: «чому строка червона і зі знаками оклику в
 * кутку?», «треба перевірити список issues він мені здається не працює»).
 *
 * Червоний рядок зі знаком «!» у куті кожної комірки — стан `orphaned` (`cell-states.css`): нічна перевірка
 * посилань (`OrphanScanPlan`) позначила рядок, бо його значення довідника (тут — «Permit number») не дійсне
 * в цьому періоді. Сітка показувала ЛИШЕ колір і знак: ні підказки в комірці, ні слова біля підпису рядка, ні
 * пункту в Issues (Issues — результати перевірки правил, а ця позначка не з них). Місяці рядка закриті
 * (`PeriodClosed`), і ще й тому оператор не міг нічого змінити — про це теж ніде не було сказано.
 *
 * Тут — причина СЛОВАМИ для рядка (поруч із підписом) і стан таблиці для спільного сховища
 * (`rowAttentionStore.ts`), з якого її читають Issues і рядок стану збереження.
 */

/**
 * Перелік назв колонок: кожна серія з трьох і більше колонок, що йдуть поспіль у зрізі, — діапазоном
 * («January–August»), решта — через кому. Порядок — порядок колонок зрізу.
 */
export function describeColumns(slice: TableSliceDto, codes: ReadonlySet<string>): string | null {
  const indices = slice.columns.flatMap((column, index) => (codes.has(column.code) ? [index] : []));
  if (indices.length === 0) return null;

  const nameAt = (index: number): string => slice.columns[index]?.header || slice.columns[index]?.code || '';
  const runs: number[][] = [];
  for (const index of indices) {
    const last = runs[runs.length - 1];
    if (last !== undefined && last[last.length - 1] === index - 1) last.push(index);
    else runs.push([index]);
  }

  return runs
    .map((run) =>
      run.length > 2 ? `${nameAt(run[0] ?? 0)}–${nameAt(run[run.length - 1] ?? 0)}` : run.map(nameAt).join(', '),
    )
    .join(', ');
}

/** Колонки зрізу, у яких сервер відмовив хоч одній комірці (рядка `rowKey`, якщо задано) через закритий період. */
export function lockedColumnsOf(slice: TableSliceDto, rowKey?: string): Set<string> {
  const rows = rowKey === undefined ? slice.rows : slice.rows.filter((row) => row.rowKey === rowKey);
  const codes = new Set<string>();
  for (const column of slice.columns) {
    if (rows.some((row) => slice.cellPermissions[cellKey(row.rowKey, column.code)] === 'PeriodClosed')) codes.add(column.code);
  }

  return codes;
}

/** Причина для рядка словами; `null` — рядок уваги не потребує. */
export function rowAttentionReason(slice: TableSliceDto, rowKey: string): string | null {
  const row = slice.rows.find((candidate) => candidate.rowKey === rowKey);
  if (row?.isOrphaned !== true) return null;

  const locked = describeColumns(slice, lockedColumnsOf(slice, rowKey));
  const orphaned = t('grid.rowAttention.orphaned');

  return locked === null ? orphaned : `${orphaned} ${t('grid.rowAttention.locked', { months: locked })}`;
}

const bySlice = new WeakMap<TableSliceDto, TableAttention>();

/** Стан таблиці для сховища; пам'ятається за об'єктом зрізу (новий зріз — новий стан, той самий — той самий). */
export function tableAttentionOf(slice: TableSliceDto): TableAttention {
  const cached = bySlice.get(slice);
  if (cached !== undefined) return cached;

  const rows: RowAttentionItem[] = [];
  for (const row of slice.rows) {
    if (row.isOrphaned !== true) continue;
    const reason = rowAttentionReason(slice, row.rowKey);
    if (reason === null) continue;
    rows.push({
      key: `${String(slice.tableInstanceId)}:${row.rowKey}`,
      tableInstanceId: slice.tableInstanceId,
      periodKey: slice.periodKey,
      rowKey: row.rowKey,
      rowLabel: row.label ?? row.rowKey,
      reason,
    });
  }

  const value: TableAttention = {
    tableInstanceId: slice.tableInstanceId,
    periodKey: slice.periodKey,
    rows,
    lockedMonths: describeColumns(slice, lockedColumnsOf(slice)),
  };
  bySlice.set(slice, value);

  return value;
}

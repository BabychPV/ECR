import type { CellProps, CellTemplate, ColumnRegular } from '@revolist/revogrid';
import type { TableSliceDto } from '@/api/types';
import { formatMonthName } from '@/shared/format/period';
import { t } from '@/shared/i18n';
import { cellKey, dayMonth, dottedDate, monthOf, type PermitGridMarks, type SplitCellMark } from './splitModel';
import type { SplitTarget } from './splitRequest';
import './permitMarks.css';

/**
 * Feature (j) in the grid, on top of the columns `gridColumns` built (the grid code itself stays as it was):
 *  - j1b: the permit cell of a row with history shows it in two lines — «KZ…A until 14.02 / KZ…B from 15.02»;
 *  - j2cell: a month cell shared by two permits — amber «Split needed» while it holds one value, then its total with a
 *    split glyph;
 *  - a calculated cell of such a row — the tooltip «not split: counts toward the permit valid on the month's last day».
 *
 * ⚠ A column nothing applies to is returned as the SAME object — RevoGrid sees no change.
 */
export function withPermitMarks(
  columns: readonly ColumnRegular[],
  slice: TableSliceDto,
  marks: PermitGridMarks,
  rowKeyOf: (model: unknown) => string,
): ColumnRegular[] {
  if (marks.rows.size === 0) return [...columns];

  const splitColumns = new Set([...marks.cells.values()].map((mark) => mark.columnCode));
  const creditedColumns = new Set([...marks.credited.keys()].map((key) => key.slice(key.indexOf(':') + 1)));
  const permitColumns = new Set([...marks.rows.values()].map((row) => row.columnCode));
  const calculated = new Set(
    slice.columns
      .filter((column) => column.dataType === 'Calculated' || (column.expression ?? null) !== null)
      .map((column) => column.code),
  );

  return columns.map((column) => {
    const code = typeof column.prop === 'string' ? column.prop : String(column.prop ?? '');
    const isPermit = permitColumns.has(code);
    const isSplit = splitColumns.has(code);
    const isCalculated = calculated.has(code) || creditedColumns.has(code);
    if (!isPermit && !isSplit && !isCalculated) return column;

    const original = column.cellTemplate;
    const originalProps = column.cellProperties;

    const cellTemplate: CellTemplate = (h, props, extra) => {
      const rowKey = rowKeyOf(props.model);
      const base = (): unknown =>
        original === undefined ? (props.value === null || props.value === undefined ? '' : String(props.value)) : original(h, props, extra);

      if (isPermit) {
        const row = marks.rows.get(rowKey);
        if (row !== undefined && row.columnCode === code) {
          return h(
            'span',
            { class: 'ecr-permit-history', 'data-permit-history-cell': '' },
            permitHistoryLines(row.permits).map((line) => h('span', { class: 'ecr-permit-history-line' }, line)),
          );
        }
      }

      const mark = isSplit ? marks.cells.get(cellKey(rowKey, code)) : undefined;
      if (mark?.needsSplit === true) {
        // F13 p220 re-check P3: a narrow month column cut «Split n…» — the cell says «Split», the full «Split needed» is
        // its accessible name and tooltip.
        return h('span', { class: 'ecr-cell-split ecr-cell-split--needed', 'data-split-cell': 'needed', 'aria-label': t('permits.cell.splitNeeded') }, [
          h('span', { class: 'ecr-cell-split-glyph', 'aria-hidden': 'true' }),
          h('span', { class: 'ecr-cell-split-label', title: t('permits.cell.splitNeeded'), 'aria-hidden': 'true' }, t('permits.cell.splitShort')),
        ]);
      }
      if (mark?.isSplit === true) {
        return h('span', { class: 'ecr-cell-split', 'data-split-cell': 'split' }, [
          h('span', { class: 'ecr-cell-split-glyph', 'aria-hidden': 'true' }),
          base() as ReturnType<typeof h>,
        ]);
      }

      return base();
    };

    const cellProperties = (props: Parameters<NonNullable<ColumnRegular['cellProperties']>>[0]): CellProps => {
      const own = (originalProps?.(props) ?? {}) as CellProps;
      const rowKey = rowKeyOf(props.model);
      const mark = isSplit ? marks.cells.get(cellKey(rowKey, code)) : undefined;
      const credited = marks.credited.get(cellKey(rowKey, code));
      const note =
        mark !== undefined
          ? splitHint(mark)
          : credited !== undefined
            ? t('permits.cell.credited', {
                month: formatMonthName(monthOf(credited.month).month),
                permit: credited.permit,
                date: dottedDate(credited.lastDay),
              })
            : calculated.has(code) && marks.rows.has(rowKey)
              ? t('permits.history.calculatedNotSplit')
              : null;
      if (note === null) return own;

      const title = typeof own['title'] === 'string' && own['title'].length > 0 ? `${own['title']}\n${note}` : note;
      const extraClass = mark?.needsSplit === true ? 'ecr-cell-split-needed' : mark?.isSplit === true ? 'ecr-cell-split-done' : null;

      return { ...own, title, ...(extraClass === null ? {} : { class: joinClass(own['class'], extraClass) }) } as CellProps;
    };

    return { ...column, cellTemplate, cellProperties };
  });
}

/**
 * j2: the cell an editor is about to open on (`beforeeditstart`) — a month shared by two permits gives the split to
 * open instead of the editor; any other cell gives `null` (the normal editor opens).
 */
export function splitTargetOfEditStart(
  marks: PermitGridMarks,
  detail: { readonly prop?: unknown; readonly model?: unknown },
  table: Omit<SplitTarget, 'rowKey' | 'columnCode'>,
  rowKeyOf: (model: unknown) => string,
): SplitTarget | null {
  const rowKey = rowKeyOf(detail.model);
  const columnCode = typeof detail.prop === 'string' ? detail.prop : String(detail.prop ?? '');

  return marks.cells.has(cellKey(rowKey, columnCode)) ? { ...table, rowKey, columnCode } : null;
}

/** «KZ…A until 14.02», «KZ…B from 15.02» — what the permit cell of a row with history shows. */
export function permitHistoryLines(permits: readonly { permit: string; from: string | null; until: string | null }[]): string[] {
  return permits.map((item) =>
    item.from === null
      ? item.until === null
        ? item.permit
        : `${item.permit} ${t('permits.cell.until', { date: dayMonth(item.until) })}`
      : `${item.permit} ${t('permits.cell.from', { date: dayMonth(item.from) })}`,
  );
}

function splitHint(mark: SplitCellMark): string {
  const { month } = monthOf(mark.month.month);
  const name = formatMonthName(month);

  return mark.needsSplit
    ? `${t('permits.cell.splitHint', { month: name })}\n${t('permits.split.limitsOnTotal')}`
    : `${t('permits.cell.splitDone', { month: name })}\n${t('permits.split.limitsOnTotal')}`;
}

function joinClass(own: unknown, extra: string): string {
  if (typeof own === 'string') return own.length === 0 ? extra : `${own} ${extra}`;
  if (typeof own === 'object' && own !== null) {
    const names = Object.entries(own as Record<string, unknown>)
      .filter(([, on]) => on === true)
      .map(([name]) => name);

    return [...names, extra].join(' ');
  }

  return extra;
}

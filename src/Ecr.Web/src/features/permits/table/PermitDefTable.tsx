// Споживач: PM-07 (сторінки пермітів: рядок-форма, вставка з Excel, широка таблиця).
import { useLayoutEffect, useMemo, useRef, useState, type JSX, type KeyboardEvent, type ReactNode, type RefObject } from 'react';
import { SegmentedControl, VisuallyHidden } from '@mantine/core';
import { formatDate } from '@/shared/format/datetime';
import { formatNumber } from '@/shared/format/number';
import { t } from '@/shared/i18n';
import { isEmptyCell, type PermitCellValue, type PermitColumn, type PermitRow } from '../lib/columns';
import {
  collapsedForMode,
  defaultCollapsed,
  groupNames,
  groupSpans,
  isWide,
  summaryValue,
  toggleGroup,
  visibleItems,
  type ColumnMode,
  type VisibleItem,
} from '../lib/wideTable';
import './permitDefTable.css';

export interface PermitTotals {
  /** Підпис у першому стовпці рядка підсумків: «Σ all 43 rows». */
  readonly label: string;
  /** Підсумки за кодом числового стовпця; стовпець без запису лишається порожнім. */
  readonly sums: Readonly<Record<string, number>>;
}

export interface PermitDefTableProps {
  readonly columns: readonly PermitColumn[];
  readonly rows: readonly PermitRow[];
  /** Назва таблиці: `<caption>` для читалки й імʼя прокручуваної області. */
  readonly caption: string;
  readonly totals?: PermitTotals | undefined;
  /** Клік по рядку (або Enter/Пробіл на ньому) — відкрити шторку рядка. */
  readonly onRowClick?: ((row: PermitRow) => void) | undefined;
  /** Попередження для комірки: текст повідомлення або `null`. */
  readonly warnCell?: ((row: PermitRow, column: PermitColumn) => string | null) | undefined;
  /** З якого режиму починати. За замовчуванням: широка таблиця — `grouped`, вузька — `all`. */
  readonly initialMode?: ColumnMode | undefined;
}

const FractionDigits = { small: 5, normal: 3 } as const;

function formatCell(column: PermitColumn, value: PermitCellValue): ReactNode {
  if (isEmptyCell(value)) return null;

  switch (column.type) {
    case 'number':
      if (typeof value !== 'number') return String(value);
      return formatNumber(value, {
        maximumFractionDigits: Math.abs(value) < 1 && value !== 0 ? FractionDigits.small : FractionDigits.normal,
      });
    case 'date': {
      // ⚠ `formatDate` сам читає `РРРР-ММ-ДД` як місцеву північ (календарна дата без години),
      // тому день не зсувається; зону тут задавати не можна — вона якраз зсунула б.
      const text = formatDate(String(value));
      return text === '' ? String(value) : text;
    }
    case 'bool':
      return value === true ? t('permits.table.yes') : value === false ? t('permits.table.no') : String(value);
    default:
      return String(value);
  }
}

function Chevron(): JSX.Element {
  return (
    <svg width={12} height={12} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2} aria-hidden="true" focusable="false">
      <path d="M9 6l6 6-6 6" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  );
}

/**
 * Ширина липкого першого стовпця → `--pm-first-w` на таблиці. Підписи решти стовпців (`.pm-stick`) липнуть до
 * ЦІЄЇ межі, а не до краю області: інакше при горизонтальній прокрутці стовпець, що наполовину заїхав під перший,
 * показує в тілі хвіст тексту, а в шапці — порожнє місце (підпис сховано під першим стовпцем). Дефект 02.11.
 */
function useFirstColumnWidth(layout: unknown): RefObject<HTMLTableElement | null> {
  const ref = useRef<HTMLTableElement | null>(null);
  useLayoutEffect(() => {
    const table = ref.current;
    const first = table?.querySelector<HTMLElement>('thead tr.pm-cols > .pm-first');
    if (table === null || first === null || first === undefined) return undefined;
    // Рядок груп може бути вищим за 30 px (кнопка групи): тоді липкий рядок стовпців заїжджав би під нього.
    const groups = table.querySelector<HTMLElement>('thead tr.pm-grp');
    const apply = (): void => {
      table.style.setProperty('--pm-first-w', `${String(first.getBoundingClientRect().width)}px`);
      if (groups !== null) table.style.setProperty('--pm-grp-height', `${String(groups.getBoundingClientRect().height)}px`);
    };
    apply();
    if (typeof ResizeObserver === 'undefined') return undefined;
    const observer = new ResizeObserver(apply);
    observer.observe(first);
    if (groups !== null) observer.observe(groups);
    return () => observer.disconnect();
  }, [layout]);
  return ref;
}

function summaryHeader(item: Extract<VisibleItem, { kind: 'summary' }>): string {
  return item.columns.every((column) => column.type === 'number')
    ? t('permits.table.sumOf', { count: item.columns.length })
    : t('permits.table.groupCount', { count: item.columns.length });
}

/**
 * Широка таблиця перміта за ОПИСОМ стовпців (макет `DefTable`): довільна кількість стовпців,
 * групи згортаються до одного підсумкового стовпця. Дві липкі смуги шапки (рядок груп + рядок
 * стовпців), липкий перший стовпець (заголовок рядка), підсумки внизу, обовʼязкові порожні комірки
 * позначені словом «Required», попередження — тлом і текстом для читалки.
 *
 * Клавіатура: кнопки груп — `aria-expanded`; прокручувана область і (за `onRowClick`) кожен рядок
 * фокусуються Tab-ом, Enter/Пробіл на рядку відкриває його. Перемикач «Grouped / All columns»
 * зʼявляється лише в широкій таблиці (> 12 стовпців).
 */
export function PermitDefTable({
  columns,
  rows,
  caption,
  totals,
  onRowClick,
  warnCell,
  initialMode,
}: PermitDefTableProps): JSX.Element {
  const wide = isWide(columns);
  const startMode: ColumnMode = initialMode ?? (wide ? 'grouped' : 'all');
  const [mode, setMode] = useState<ColumnMode>(startMode);
  const [collapsed, setCollapsed] = useState<ReadonlySet<string>>(() =>
    initialMode === undefined ? defaultCollapsed(columns) : collapsedForMode(columns, initialMode),
  );

  const items = useMemo(() => visibleItems(columns, collapsed), [columns, collapsed]);
  const hasGroups = useMemo(() => groupNames(columns).length > 0, [columns]);
  const spans = useMemo(() => groupSpans(items), [items]);
  const tableRef = useFirstColumnWidth(items);

  function onKeyDown(event: KeyboardEvent<HTMLTableRowElement>, row: PermitRow): void {
    if (event.target !== event.currentTarget) return;
    if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault();
      onRowClick?.(row);
    }
  }

  return (
    <div>
      {wide && (
        <div className="pm-table-bar">
          <SegmentedControl
            size="xs"
            aria-label={t('permits.table.columns')}
            value={mode}
            onChange={(value) => {
              const next: ColumnMode = value === 'all' ? 'all' : 'grouped';
              setMode(next);
              setCollapsed(collapsedForMode(columns, next));
            }}
            data={[
              { value: 'grouped', label: t('permits.table.grouped') },
              { value: 'all', label: t('permits.table.allColumns') },
            ]}
          />
        </div>
      )}

      <div className="pm-tw" role="region" aria-label={caption} tabIndex={0}>
        <table className="pm-t" ref={tableRef}>
          <caption className="mantine-visually-hidden">{caption}</caption>
          <thead>
            {hasGroups && (
              <tr className="pm-grp">
                {spans.map((span) =>
                  span.group === null ? (
                    <td key={`g-${span.start}`} colSpan={span.span} className={span.start === 0 ? 'pm-first' : undefined} />
                  ) : (
                    <th key={`g-${span.start}`} colSpan={span.span} scope="colgroup">
                      <button
                        type="button"
                        className="pm-gbtn"
                        aria-expanded={!collapsed.has(span.group)}
                        onClick={() => {
                          const group = span.group;
                          if (group !== null) setCollapsed((current) => toggleGroup(current, group));
                        }}
                      >
                        <Chevron />
                        {span.group}
                        {collapsed.has(span.group) && (
                          <span className="pm-faint">{t('permits.table.groupCount', { count: columnsInGroup(columns, span.group) })}</span>
                        )}
                      </button>
                    </th>
                  ),
                )}
              </tr>
            )}
            <tr className="pm-cols">
              {items.map((item, index) => {
                if (item.kind === 'summary') {
                  return (
                    <th key={`s-${item.group}`} scope="col" className="num">
                      <span className="pm-stick">{summaryHeader(item)}</span>
                    </th>
                  );
                }
                const { column } = item;
                const label = (
                  <>
                    {column.label}
                    {column.unit !== undefined && <span className="pm-faint">{` ${column.unit}`}</span>}
                    {column.required === true && column.derived !== true && (
                      <>
                        <span aria-hidden="true">{' *'}</span>
                        <VisuallyHidden>{`, ${t('permits.table.required')}`}</VisuallyHidden>
                      </>
                    )}
                  </>
                );
                return (
                  <th
                    key={column.code}
                    scope="col"
                    className={`${column.type === 'number' ? 'num' : ''} ${index === 0 ? 'pm-first' : ''}`.trim()}
                    title={column.code}
                  >
                    {index === 0 ? label : <span className="pm-stick">{label}</span>}
                  </th>
                );
              })}
            </tr>
          </thead>
          <tbody>
            {rows.map((row) => (
              <tr
                key={row.id}
                tabIndex={onRowClick === undefined ? undefined : 0}
                data-clickable={onRowClick === undefined ? undefined : ''}
                onClick={onRowClick === undefined ? undefined : () => onRowClick(row)}
                onKeyDown={onRowClick === undefined ? undefined : (event) => onKeyDown(event, row)}
              >
                {items.map((item, index) => {
                  if (item.kind === 'summary') {
                    const sum = summaryValue(row, item.columns);
                    return (
                      <td key={`s-${item.group}`} className="num">
                        {sum === null ? (
                          <span className="pm-faint pm-stick">…</span>
                        ) : (
                          <span className="pm-stick">{formatNumber(sum, { maximumFractionDigits: FractionDigits.normal })}</span>
                        )}
                      </td>
                    );
                  }

                  const { column } = item;
                  const value = row[column.code];
                  const missing = column.required === true && column.derived !== true && isEmptyCell(value);
                  const warning = missing || warnCell === undefined ? null : warnCell(row, column);
                  const shown = formatCell(column, value);
                  const classes = [
                    column.type === 'number' ? 'num' : '',
                    column.derived === true ? 'pm-derived' : '',
                    missing ? 'pm-bad' : '',
                    warning !== null ? 'pm-warn' : '',
                  ]
                    .filter(Boolean)
                    .join(' ');
                  const content = missing ? (
                    <span>{t('permits.table.required')}</span>
                  ) : shown === null ? (
                    <span className="pm-faint">—</span>
                  ) : (
                    shown
                  );

                  // Перша комірка рядка — його заголовок (`th scope="row"`), решта — дані.
                  const Cell = index === 0 ? 'th' : 'td';
                  return (
                    <Cell
                      key={column.code}
                      scope={index === 0 ? 'row' : undefined}
                      className={index === 0 ? `${classes} pm-first`.trim() : classes}
                      data-required-empty={missing ? 'true' : undefined}
                      title={warning ?? undefined}
                    >
                      {index === 0 ? content : <span className="pm-stick">{content}</span>}
                      {warning !== null && <VisuallyHidden>{`. ${warning}`}</VisuallyHidden>}
                    </Cell>
                  );
                })}
              </tr>
            ))}
          </tbody>
          {totals !== undefined && (
            <tfoot>
              <tr>
                {items.map((item, index) => {
                  if (item.kind === 'summary') return <td key={`s-${item.group}`} />;
                  if (index === 0) {
                    return (
                      <th key={item.column.code} scope="row" className="pm-first">
                        {totals.label}
                      </th>
                    );
                  }
                  const sum = totals.sums[item.column.code];
                  return (
                    <td key={item.column.code} className="num">
                      {sum === undefined ? null : <span className="pm-stick">{formatNumber(sum, { maximumFractionDigits: 5 })}</span>}
                    </td>
                  );
                })}
              </tr>
            </tfoot>
          )}
        </table>
      </div>
    </div>
  );
}

function columnsInGroup(columns: readonly PermitColumn[], group: string): number {
  return columns.filter((column) => column.group === group).length;
}

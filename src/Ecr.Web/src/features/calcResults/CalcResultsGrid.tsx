import { useEffect, useRef, useState, type JSX, type ReactNode } from 'react';
import { formatDecimal } from '@/shared/format';
import { t } from '@/shared/i18n';
import { localized } from '@/shared/i18n/localized';
import type { CalculationYearMonth, CalculationYearRow, CalculationYearView } from './api';
import type { GridItem } from './model';

/** Висота рядка сітки (px) — та сама, що в CSS (`.ecr-cr-grid td`). */
export const RowHeight = 32;
/** Скільки рядків малювати понад видиму область з кожного боку. */
const Overscan = 24;

/** Підписи, спільні для сітки й шторки. */
export interface ResultLabels {
  readonly equipment: (row: CalculationYearRow) => string;
  readonly output: (row: CalculationYearRow) => ReactNode;
  readonly outputText: (row: CalculationYearRow) => string;
  readonly unit: (row: CalculationYearRow) => string;
  readonly month: (month: number) => string;
}

interface CalcResultsGridProps {
  readonly view: CalculationYearView;
  readonly items: readonly GridItem[];
  readonly collapsed: ReadonlySet<string>;
  readonly labels: ResultLabels;
  /** Річна розкладка: обраний «місяць результатів» (1…12); `null` — помісячні копії. */
  readonly pickedMonth: number | null;
  readonly onToggle: (key: string) => void;
  /** Заголовок місяця: у річній розкладці обирає місяць результатів, інакше відкриває документ на ньому. */
  readonly onMonth: (month: CalculationYearMonth) => void;
  readonly onValue: (row: CalculationYearRow, monthIndex: number) => void;
}

function Chevron(): JSX.Element {
  return (
    <svg width={14} height={14} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={1.75} aria-hidden="true" focusable="false">
      <path d="M9 6l6 6-6 6" />
    </svg>
  );
}

/** Стан місяця словами (заголовок колонки). */
export function monthStateText(month: CalculationYearMonth): string {
  if (month.runState === 'notOpened') return t('calcResults.month.notOpened');
  if (month.runState === 'noRun') return t('calcResults.month.noRun');
  if (month.runState === 'outOfDate') return t('calcResults.month.outOfDate');
  return month.periodState === 'closed' ? t('calcResults.month.closed') : t('calcResults.month.current');
}

/**
 * Значення місяця словами. ⚠ Десяткове API — рядок без втрати знаків (D-30); схема описує елемент масиву
 * `decimal?` як `unknown`, тож тип перевіряється тут, а не довіряється.
 */
export function valueText(value: unknown): string | null {
  if (typeof value !== 'string' && typeof value !== 'number') return null;
  return formatDecimal(value) ?? String(value);
}

/** Сума за рік (лише адитивні виходи). */
export function totalText(row: CalculationYearRow): string | null {
  return valueText(row.yearTotal);
}

/**
 * Віртуалізована сітка «Calculation results» (без нової залежності): малюються лише видимі рядки плюс запас,
 * висота решти тримається порожніми рядками-розпірками. Таблична семантика збережена (`aria-rowcount`,
 * `aria-rowindex`), шапка й перші дві колонки — закріплені.
 */
export function CalcResultsGrid({
  view,
  items,
  collapsed,
  labels,
  pickedMonth,
  onToggle,
  onMonth,
  onValue,
}: CalcResultsGridProps): JSX.Element {
  const wrap = useRef<HTMLDivElement>(null);
  const [scrollTop, setScrollTop] = useState(0);
  const [height, setHeight] = useState(800);

  useEffect(() => {
    const node = wrap.current;
    if (node === null) return undefined;
    const measure = (): void => setHeight(node.clientHeight > 0 ? node.clientHeight : 800);
    measure();
    if (typeof ResizeObserver === 'undefined') return undefined;
    const observer = new ResizeObserver(measure);
    observer.observe(node);
    return () => observer.disconnect();
  }, []);

  const start = Math.max(0, Math.floor(scrollTop / RowHeight) - Overscan);
  const end = Math.min(items.length, Math.ceil((scrollTop + height) / RowHeight) + Overscan);
  const visible = items.slice(start, end);
  /** Колонки за смугою: місяці + сума за рік. */
  const span = view.months.length + 1;
  /** Усі колонки рядка: вихід, одиниця, місяці, сума — розпірки займають їх цілком. */
  const columnCount = span + 2;

  const band = (item: Extract<GridItem, { kind: 'band' }>, index: number): JSX.Element => {
    const equipment = view.equipment[item.row.equipment];
    const table = view.tables[equipment?.table ?? 0];
    const methodology =
      item.row.methodology === null || item.row.methodology === undefined ? null : view.methodologies[item.row.methodology];
    let content: ReactNode;
    if (item.level === 0) {
      content = t('calcResults.sheetBand', { name: localized(table?.sheetNameL10n) || (table?.sheetCode ?? '') });
    } else if (item.level === 1) {
      content =
        table === undefined || table.tableDefId === 0
          ? t('calcResults.noTable')
          : `${table.tableCode} ${localized(table.tableNameL10n)}`.trim();
    } else {
      content = (
        <>
          <span>{labels.equipment(item.row)}</span>
          {equipment?.idCode !== null && equipment?.idCode !== undefined && equipment.idCode !== '' && (
            <span className="ecr-cr-code" title={t('calcResults.idCode')}>{equipment.idCode}</span>
          )}
          <span className="ecr-cr-muted">
            {methodology === null || methodology === undefined
              ? t('calcResults.templateFormula')
              : `${localized(methodology.nameL10n) || methodology.code} · v${methodology.version}`}
          </span>
        </>
      );
    }
    const open = !collapsed.has(item.key);
    return (
      <tr key={`b:${item.key}`} className={`ecr-cr-band ecr-cr-band--${String(item.level)}`} aria-rowindex={index + 2}>
        <th scope="rowgroup" colSpan={2}>
          <button type="button" className="ecr-cr-toggle" aria-expanded={open} onClick={() => onToggle(item.key)} data-cr-group={item.key}>
            <Chevron />
            {content}
          </button>
        </th>
        <td colSpan={span} />
      </tr>
    );
  };

  const cell = (row: CalculationYearRow, i: number): JSX.Element => {
    const month = view.months[i];
    const state = row.states[i];
    const text = valueText(row.values[i]);
    const pick = pickedMonth === i + 1 ? ' ecr-cr-v--pick' : '';
    const monthName = labels.month(i + 1);
    if (state !== 'v' || text === null) {
      const title =
        state === 'n'
          ? t('calcResults.cell.noRun', { month: monthName })
          : t('calcResults.cell.absent', { month: monthName });
      return <td key={i} className={`ecr-cr-v ecr-cr-v--none${pick}`} title={title} aria-label={title} />;
    }
    const stale = month?.runState === 'outOfDate';
    return (
      <td key={i} className={`ecr-cr-v${stale ? ' ecr-cr-v--stale' : ''}${pick}`}>
        <button
          type="button"
          className="ecr-cr-vb"
          title={`${labels.outputText(row)} · ${labels.equipment(row)} · ${monthName}${stale ? ` — ${t('calcResults.month.outOfDate')}` : ''}`}
          onClick={() => onValue(row, i)}
          data-cr-cell={`${row.id}#${String(i + 1)}`}
        >
          {text}
        </button>
      </td>
    );
  };

  const total = (row: CalculationYearRow): JSX.Element => {
    const unit = row.unit === null || row.unit === undefined ? null : view.units[row.unit];
    if (unit === null || unit === undefined || !unit.additive) {
      return (
        <td className="ecr-cr-tot ecr-cr-tot--na" title={t('calcResults.notAdditive', { unit: labels.unit(row) })}>
          —
        </td>
      );
    }
    const text = totalText(row);
    if (text === null) return <td className="ecr-cr-tot ecr-cr-tot--na">—</td>;
    const stale = view.months.some((m, i) => m.runState === 'outOfDate' && row.states[i] === 'v');
    return (
      <td className={`ecr-cr-tot${stale ? ' ecr-cr-tot--warn' : ''}`} title={t('calcResults.totalOf', { n: row.monthsIncluded })}>
        {text}
        <small>{`${String(row.monthsIncluded)}/12`}</small>
      </td>
    );
  };

  return (
    <div
      ref={wrap}
      className="ecr-cr-wrap"
      // Прокручувана область має бути досяжна з клавіатури (WCAG 2.1.1, axe `scrollable-region-focusable`).
      tabIndex={0}
      role="region"
      aria-label={t('calcResults.gridLabel')}
      onScroll={(event) => setScrollTop(event.currentTarget.scrollTop)}
      data-testid="calc-results-grid"
    >
      <table className="ecr-cr-grid" aria-rowcount={items.length + 1}>
        <thead>
          <tr aria-rowindex={1}>
            <th scope="col" className="ecr-cr-out">{t('calcResults.colOutput')}</th>
            <th scope="col" className="ecr-cr-unit">{t('calcResults.colUnit')}</th>
            {view.months.map((month) => {
              const cls = `ecr-cr-m${month.runState === 'outOfDate' ? ' ecr-cr-m--stale' : ''}${pickedMonth === month.month ? ' ecr-cr-m--pick' : ''}`;
              return (
                <th key={month.month} scope="col" className={cls}>
                  <button
                    type="button"
                    className="ecr-cr-mh"
                    onClick={() => onMonth(month)}
                    aria-pressed={pickedMonth === null ? undefined : pickedMonth === month.month}
                    title={pickedMonth === null ? t('calcResults.monthOpen', { month: labels.month(month.month) }) : t('calcResults.monthPick', { month: labels.month(month.month) })}
                    data-cr-month={month.month}
                  >
                    <span>{labels.month(month.month)}</span>
                    <small>{monthStateText(month)}</small>
                  </button>
                </th>
              );
            })}
            <th scope="col" className="ecr-cr-tot">
              {t('calcResults.colTotal', { year: view.year })}
              <small>{t('calcResults.colTotalHint')}</small>
            </th>
          </tr>
        </thead>
        <tbody>
          {start > 0 && (
            <tr aria-hidden="true" style={{ height: start * RowHeight }}>
              <td colSpan={columnCount} />
            </tr>
          )}
          {visible.map((item, offset) =>
            item.kind === 'band' ? (
              band(item, start + offset)
            ) : (
              <tr key={`o:${item.key}`} className="ecr-cr-out" aria-rowindex={start + offset + 2} data-cr-row={item.row.id}>
                <th scope="row" title={`${labels.outputText(item.row)} — ${item.row.outputCode}`}>{labels.output(item.row)}</th>
                <td className="ecr-cr-unit">{labels.unit(item.row)}</td>
                {view.months.map((_, i) => cell(item.row, i))}
                {total(item.row)}
              </tr>
            ),
          )}
          {end < items.length && (
            <tr aria-hidden="true" style={{ height: (items.length - end) * RowHeight }}>
              <td colSpan={columnCount} />
            </tr>
          )}
        </tbody>
      </table>
    </div>
  );
}

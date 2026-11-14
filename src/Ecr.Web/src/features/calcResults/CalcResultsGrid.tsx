import { useEffect, useRef, useState, type JSX, type ReactNode } from 'react';
import { formatDecimal } from '@/shared/format';
import { t } from '@/shared/i18n';
import { localized } from '@/shared/i18n/localized';
import type { CalculationYearMonth } from './api';
import type { GridItem, ResultRow } from './model';

/** Висота рядка сітки (px) — та сама, що в CSS (`.ecr-cr-grid td`). */
export const RowHeight = 32;
/** Скільки рядків малювати понад видиму область з кожного боку. */
const Overscan = 24;

/** Підказка рядка виходу: «назва — код», а коли назви немає (текст і є кодом) — код один раз (B77). */
function outputTitle(text: string, code: string): string {
  return text === '' || text === code ? code : `${text} — ${code}`;
}

/** Підписи, спільні для сітки й шторки. */
export interface ResultLabels {
  readonly equipment: (row: ResultRow) => string;
  readonly output: (row: ResultRow) => ReactNode;
  readonly outputText: (row: ResultRow) => string;
  readonly unit: (row: ResultRow) => string;
  readonly month: (month: number) => string;
}

interface CalcResultsGridProps {
  readonly months: readonly CalculationYearMonth[];
  readonly year: number;
  readonly items: readonly GridItem[];
  /** Згорнуті аркуші й групи обладнання. */
  readonly collapsed: ReadonlySet<string>;
  /** Розгорнуті таблиці. */
  readonly open: ReadonlySet<string>;
  /** Таблиці, сторінка яких зараз читається. */
  readonly loading: ReadonlySet<string>;
  readonly labels: ResultLabels;
  /** Річна розкладка: обраний «місяць результатів» (1…12); `null` — помісячні копії. */
  readonly pickedMonth: number | null;
  readonly onToggle: (key: string) => void;
  readonly onToggleTable: (key: string) => void;
  /** Рядок «ще» таблиці потрапив у видиму область (або натиснуто): дочитати наступну сторінку. */
  readonly onMore: (table: string, loaded: number) => void;
  /** Заголовок місяця: у річній розкладці обирає місяць результатів, інакше відкриває документ на ньому. */
  readonly onMonth: (month: CalculationYearMonth) => void;
  readonly onValue: (row: ResultRow, monthIndex: number) => void;
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
export function totalText(row: ResultRow): string | null {
  return valueText(row.yearTotal);
}

/**
 * Віртуалізована сітка «Calculation results» (без нової залежності): малюються лише видимі рядки плюс запас,
 * висота решти тримається порожніми рядками-розпірками. Таблична семантика збережена (`aria-rowcount`,
 * `aria-rowindex` — по ВСІХ рядках переліку, включно зі смугами й рядками «ще»), шапка й перші дві колонки закріплені.
 */
export function CalcResultsGrid({
  months,
  year,
  items,
  collapsed,
  open,
  loading,
  labels,
  pickedMonth,
  onToggle,
  onToggleTable,
  onMore,
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
  const span = months.length + 1;
  /** Усі колонки рядка: вихід, одиниця, місяці, сума — розпірки займають їх цілком. */
  const columnCount = span + 2;

  // Рядок «ще» у видимій області — дочитати наступну сторінку його таблиці (сторінки йдуть підряд).
  const wanted = visible
    .filter((item): item is Extract<GridItem, { kind: 'more' }> => item.kind === 'more' && !loading.has(item.table.key))
    .map((item) => `${item.table.key}|${String(item.loaded)}`)
    .join(',');
  useEffect(() => {
    if (wanted === '') return;
    for (const entry of wanted.split(',')) {
      const bar = entry.lastIndexOf('|');
      onMore(entry.slice(0, bar), Number(entry.slice(bar + 1)));
    }
  }, [wanted, onMore]);

  const toggle = (key: string, isOpen: boolean, onClick: () => void, content: ReactNode): JSX.Element => (
    <button type="button" className="ecr-cr-toggle" aria-expanded={isOpen} onClick={onClick} data-cr-group={key}>
      <Chevron />
      {content}
    </button>
  );

  const band = (item: Extract<GridItem, { kind: 'sheet' | 'table' | 'group' }>, index: number): JSX.Element => {
    let level: number;
    let content: ReactNode;
    let button: JSX.Element;
    if (item.kind === 'sheet') {
      level = 0;
      content = t('calcResults.sheetBand', { name: localized(item.table.sheetNameL10n) || item.table.sheetCode });
      button = toggle(item.key, !collapsed.has(item.key), () => onToggle(item.key), content);
    } else if (item.kind === 'table') {
      level = 1;
      const table = item.table;
      content = (
        <>
          <span>{table.tableDefId === 0 ? t('calcResults.noTable') : `${table.tableCode} ${localized(table.tableNameL10n)}`.trim()}</span>
          <span className="ecr-cr-muted" data-cr-count={table.key}>{String(table.matched)}</span>
        </>
      );
      button = toggle(item.key, open.has(item.key), () => onToggleTable(item.key), content);
    } else {
      level = 2;
      const { row } = item;
      content = (
        <>
          <span>{labels.equipment(row)}</span>
          {row.equipment.idCode !== null && row.equipment.idCode !== undefined && row.equipment.idCode !== '' && (
            <span className="ecr-cr-code" title={t('calcResults.idCode')}>{row.equipment.idCode}</span>
          )}
          <span className="ecr-cr-muted">
            {row.methodology === null
              ? t('calcResults.templateFormula')
              : `${localized(row.methodology.nameL10n) || row.methodology.code} · v${row.methodology.version}`}
          </span>
        </>
      );
      button = toggle(item.key, !collapsed.has(item.key), () => onToggle(item.key), content);
    }
    return (
      <tr key={`b:${item.key}`} className={`ecr-cr-band ecr-cr-band--${String(level)}`} aria-rowindex={index + 2}>
        <th scope="rowgroup" colSpan={2}>
          {button}
        </th>
        <td colSpan={span} />
      </tr>
    );
  };

  const more = (item: Extract<GridItem, { kind: 'more' }>, index: number): JSX.Element => {
    const busy = loading.has(item.table.key);
    return (
      <tr key={`m:${item.key}`} className="ecr-cr-more" aria-rowindex={index + 2} data-cr-more={item.table.key}>
        <th scope="row" colSpan={2}>
          {busy ? (
            <span className="ecr-cr-muted" role="status">{t('calcResults.loadingRows')}</span>
          ) : (
            <button type="button" className="ecr-cr-toggle" onClick={() => onMore(item.table.key, item.loaded)}>
              {t('calcResults.moreRows', { shown: item.loaded, total: item.total })}
            </button>
          )}
        </th>
        <td colSpan={span} />
      </tr>
    );
  };

  const cell = (row: ResultRow, i: number): JSX.Element => {
    const month = months[i];
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

  const total = (row: ResultRow): JSX.Element => {
    if (row.unit === null || !row.unit.additive) {
      return (
        <td className="ecr-cr-tot ecr-cr-tot--na" title={t('calcResults.notAdditive', { unit: labels.unit(row) })}>
          —
        </td>
      );
    }
    const text = totalText(row);
    if (text === null) return <td className="ecr-cr-tot ecr-cr-tot--na">—</td>;
    const stale = months.some((m, i) => m.runState === 'outOfDate' && row.states[i] === 'v');
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
            {months.map((month) => {
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
              {t('calcResults.colTotal', { year })}
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
          {visible.map((item, offset) => {
            const index = start + offset;
            if (item.kind === 'more') return more(item, index);
            if (item.kind !== 'output') return band(item, index);
            return (
              <tr key={`o:${item.key}`} className="ecr-cr-out" aria-rowindex={index + 2} data-cr-row={item.row.id}>
                <th scope="row" title={outputTitle(labels.outputText(item.row), item.row.outputCode)}>{labels.output(item.row)}</th>
                <td className="ecr-cr-unit">{labels.unit(item.row)}</td>
                {months.map((_, i) => cell(item.row, i))}
                {total(item.row)}
              </tr>
            );
          })}
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

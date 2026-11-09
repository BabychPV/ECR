import type { JSX } from 'react';
import { VisuallyHidden } from '@mantine/core';
import { Link } from 'react-router-dom';
import type { components } from '@/api/schema';
import type { PeriodCalendarDto } from '@/api/types';
import { formatMonthName } from '@/shared/format';
import { t } from '@/shared/i18n';
import { StripLegend, stripCell, type StripCell } from './yearStripModel';

type DocumentPeriodState = components['schemas']['DocumentPeriodState'];

/** Місяців у смузі року — завжди 12 (людина 07.11: «12-month strip»). */
const Months = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12] as const;

/**
 * Підпис стану клітинки — ЛІТЕРАЛАМИ ключів (сторож каталогу `EndpointCoverageTests` читає літерали).
 */
export function stripCellLabel(cell: StripCell): string {
  switch (cell) {
    case 'open':
      return t('documents.strip.open');
    case 'late':
      return t('documents.strip.late');
    case 'submitted':
      return t('documents.strip.submitted');
    case 'approved':
      return t('documents.strip.approved');
    case 'returned':
      return t('documents.strip.returned');
    case 'locked':
      return t('documents.strip.locked');
    case 'notOpen':
      return t('documents.strip.notOpen');
    default:
      return t('documents.strip.unknown');
  }
}

/** Знак у клітинці (не лише колір): ✓ затверджено, ✕ — повернуто, ! — пізні правки; замок — заблоковано (SVG, не емодзі). */
const Marks: Readonly<Partial<Record<StripCell, string>>> = { approved: '✓', returned: '✕', late: '!' };

function LockMark(): JSX.Element {
  return (
    <svg width="9" height="9" viewBox="0 0 12 12" aria-hidden="true" focusable="false">
      <path d="M2 5.5h8V11H2z" fill="currentColor" />
      <path d="M4 5.5V4a2 2 0 0 1 4 0v1.5" fill="none" stroke="currentColor" strokeWidth="1.4" />
    </svg>
  );
}

function CellBox({ cell, current = false }: { readonly cell: StripCell; readonly current?: boolean }): JSX.Element {
  return (
    <span className="ecr-year-strip-box" data-cell={cell} data-current={current ? 'true' : undefined} aria-hidden="true">
      {cell === 'locked' ? <LockMark /> : (Marks[cell] ?? '')}
    </span>
  );
}

/**
 * ✎ Period clarity (людина 07.11, п. e) + ✎ g3 (макет 08.11): смуга 12 місяців року в рядку документа. Клітинка —
 * посилання, що відкриває документ на цьому місяці (`/documents/{id}?periodKey=…`), з `aria-label`
 * «March 2026: Locked» — стан не лише кольором. Стан — `stripCell` (погодження документа + стан періоду проєкту).
 */
export function DocumentYearStrip({
  documentId,
  businessKey,
  year,
  periodStates,
  calendar,
}: {
  readonly documentId: number;
  readonly businessKey: string;
  readonly year: number;
  readonly periodStates: readonly DocumentPeriodState[] | null | undefined;
  /** Календар проєкту документа; `undefined` — ще не приїхав (неподані місяці — «unknown»). */
  readonly calendar: PeriodCalendarDto | undefined;
}): JSX.Element {
  const byKey = new Map((periodStates ?? []).map((entry) => [entry.periodKey, entry.state]));
  const periodByKey = new Map((calendar?.periods ?? []).map((period) => [period.periodKey, period]));

  return (
    <ul className="ecr-year-strip" aria-label={t('documents.yearStripLabel', { key: businessKey, year })}>
      {Months.map((month) => {
        const periodKey = year * 100 + month;
        const period = periodByKey.get(periodKey);
        const cell = stripCell(byKey.get(periodKey) ?? null, period?.state ?? null);
        const label = t('documents.monthState', { month: `${formatMonthName(month)} ${String(year)}`, state: stripCellLabel(cell) });

        return (
          <li key={month}>
            <Link
              to={`/documents/${String(documentId)}?periodKey=${String(periodKey)}`}
              className="ecr-year-strip-cell"
              aria-label={label}
              title={label}
              data-month={periodKey}
              data-cell={cell}
              data-current={period?.isCurrent === true ? 'true' : undefined}
            >
              <CellBox cell={cell} current={period?.isCurrent === true} />
            </Link>
          </li>
        );
      })}
    </ul>
  );
}

/** Заголовок колонки смуги: перші літери місяців над клітинками (як `.ms.hd` у макеті). */
export function DocumentYearStripHeader({ year }: { readonly year: number }): JSX.Element {
  return (
    <span className="ecr-year-strip-head">
      <VisuallyHidden>{t('documents.months', { year })}</VisuallyHidden>
      <span className="ecr-year-strip ecr-year-strip-letters" aria-hidden="true">
        {Months.map((month) => (
          <span key={month} className="ecr-year-strip-letter">
            {formatMonthName(month).slice(0, 1)}
          </span>
        ))}
      </span>
    </span>
  );
}

/** Легенда смуги під фільтрами (рядок g3: «з легендою»). */
export function DocumentYearStripLegend(): JSX.Element {
  return (
    <div className="ecr-year-strip-legend" role="group" aria-label={t('documents.strip.legend')} data-year-strip-legend="">
      <span className="ecr-year-strip-legend-title">{t('documents.strip.legend')}</span>
      {StripLegend.map((cell) => (
        <span key={cell} className="ecr-year-strip-legend-item">
          <CellBox cell={cell} />
          {stripCellLabel(cell)}
        </span>
      ))}
      <span className="ecr-year-strip-legend-item">
        <CellBox cell="open" current />
        {t('documents.strip.current')}
      </span>
    </div>
  );
}

import type { JSX } from 'react';
import { Link } from 'react-router-dom';
import type { components } from '@/api/schema';
import { formatMonthName } from '@/shared/format';
import { t } from '@/shared/i18n';
import { statusKey, statusTone } from '@/shared/ui/StatusBadge';

type DocumentPeriodState = components['schemas']['DocumentPeriodState'];

/** Місяців у смузі року — завжди 12 (людина 07.11: «12-month strip»). */
const Months = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12] as const;

/**
 * Не лише колір: знак стану в клітинці (прихований від читалки — їй стан каже `aria-label`). Draft і Approved мають
 * той самий тон (`neutral`), тож без знака їх на око не розрізнити.
 */
const Marks: Readonly<Record<string, string>> = {
  Draft: '○',
  PartlySubmitted: '◐',
  Submitted: '●',
  Approved: '✓',
  Rejected: '✕',
};

/**
 * Підпис стану — ЛІТЕРАЛАМИ ключів (сторож каталогу `EndpointCoverageTests` розбирає `statusKey('sheet', 'Draft')`,
 * а не `statusKey('sheet', state)`), ті самі слова, що в бейджах і фільтрі.
 */
function stateLabel(state: string): string {
  switch (state) {
    case 'Draft':
      return t(statusKey('sheet', 'Draft'));
    case 'PartlySubmitted':
      return t('documents.statePartlySubmitted');
    case 'Submitted':
      return t(statusKey('sheet', 'Submitted'));
    case 'Approved':
      return t(statusKey('sheet', 'Approved'));
    case 'Rejected':
      return t(statusKey('sheet', 'Rejected'));
    default:
      return state;
  }
}

/**
 * ✎ Period clarity (людина 07.11, п. e): смуга 12 місяців року в рядку документа — стан кожного місяця з
 * `periodStates` (сервер рахує одним запитом на сторінку). Клітинка — посилання: відкриває документ на цьому місяці
 * (`/documents/{id}?periodKey=…`), у порядку табуляції, з `aria-label` «March: Submitted» — стан не лише кольором.
 *
 * ⚠ Місяць без стану (документ за нього ще не заповнювали) — теж посилання, «March: no data»: документ охоплює
 * весь рік, відкрити його можна на будь-якому місяці.
 */
export function DocumentYearStrip({
  documentId,
  businessKey,
  year,
  periodStates,
}: {
  readonly documentId: number;
  readonly businessKey: string;
  readonly year: number;
  readonly periodStates: readonly DocumentPeriodState[] | null | undefined;
}): JSX.Element {
  const byKey = new Map((periodStates ?? []).map((entry) => [entry.periodKey, entry.state]));

  return (
    <ul className="ecr-year-strip" aria-label={t('documents.yearStripLabel', { key: businessKey, year })}>
      {Months.map((month) => {
        const periodKey = year * 100 + month;
        const state = byKey.get(periodKey) ?? null;
        const name = formatMonthName(month);
        const label =
          state === null
            ? t('documents.monthNoState', { month: name })
            : t('documents.monthState', { month: name, state: stateLabel(state) });
        // Тон — той самий, яким `StatusBadge` малює `sheet/<стан>`; кольори — у `documentsList.css` за `data-tone`.
        const tone = state === null ? null : statusTone('sheet', state);

        return (
          <li key={month}>
            <Link
              to={`/documents/${String(documentId)}?periodKey=${String(periodKey)}`}
              className="ecr-year-strip-cell"
              aria-label={label}
              title={label}
              data-month={periodKey}
              data-month-state={state ?? 'none'}
              data-tone={tone ?? 'none'}
            >
              <span aria-hidden="true">{name.slice(0, 1)}</span>
              <span aria-hidden="true" className="ecr-year-strip-mark">
                {state === null ? '' : (Marks[state] ?? '?')}
              </span>
            </Link>
          </li>
        );
      })}
    </ul>
  );
}

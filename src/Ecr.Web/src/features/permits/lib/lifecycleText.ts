import { EcrApiError } from '@/api/client';
import { formatDate } from '@/shared/format';
import { t } from '@/shared/i18n';

/**
 * PUI-10: дата в кінці речення. Російське `formatDate` повертає «10 янв. 2027 г.» - крапка скорочення «г.» сама
 * закінчує речення, а крапка шаблону після `{to}` давала «г..». Тут знімається ОДНА кінцева крапка; для en/kz
 * (без крапки в кінці дати) рядок не змінюється.
 */
export function sentenceDate(text: string): string {
  return text.endsWith('.') ? text.slice(0, -1) : text;
}

/** Ключ відмови `422 ECR-PRM-0422`: дата завершення поза вікном дозволу (`PermitErrors.EndDateOutOfRange`). */
export const EndDateOutOfRangeKey = 'err.ECR-PRM-0422.endDateOutOfRange';

const IsoDate = /^\d{4}-\d{2}-\d{2}$/;

function humanDate(value: unknown): string {
  return typeof value === 'string' && IsoDate.test(value) ? formatDate(value) : '-';
}

/**
 * PUI-5: текст відмови «дата завершення поза вікном» з датами за локаллю інтерфейсу. Сервер кладе у підстановки сирі
 * ISO-дати (`2026-10-20`), а `showApiError` підставив би їх як є. Для інших відмов - `null`: їх показує `showApiError`.
 */
export function endDateOutOfRangeText(error: unknown): string | null {
  if (!(error instanceof EcrApiError)) return null;
  const extensions = error.problem.extensions2;
  if (extensions?.['messageKey'] !== EndDateOutOfRangeKey) return null;

  return t('err.ECR-PRM-0422.endDateOutOfRange', {
    endDate: humanDate(extensions['endDate']),
    from: humanDate(extensions['from']),
    to: sentenceDate(humanDate(extensions['to'])),
  });
}

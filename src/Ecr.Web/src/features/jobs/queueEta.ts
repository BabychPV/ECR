/**
 * Текст черги для задачі в стані `Queued` (PERF-recalc-scope): позиція і ПРИБЛИЗНИЙ час старту.
 * Сервер віддає `queuePosition` (1 = наступна) і `estimatedStartAt` (UTC ISO; оцінка за медіаною кроків).
 */
import { t } from '@/shared/i18n';

export interface QueueInfo {
  readonly queuePosition?: number | null | undefined;
  readonly estimatedStartAt?: string | null | undefined;
}

const SoonMs = 30_000;
const MinuteMs = 60_000;

/** Ключ і параметри рядка про очікуваний старт; `null` — оцінки немає. */
export function queueEtaOf(
  estimatedStartAt: string | null | undefined,
  now: number,
): { readonly key: 'jobs.queueEtaSoon' } | { readonly key: 'jobs.queueEta'; readonly params: { readonly minutes: number } } | null {
  if (estimatedStartAt === null || estimatedStartAt === undefined) return null;
  const at = Date.parse(estimatedStartAt);
  if (Number.isNaN(at)) return null;
  const left = at - now;
  if (left < SoonMs) return { key: 'jobs.queueEtaSoon' };
  return { key: 'jobs.queueEta', params: { minutes: Math.max(1, Math.ceil(left / MinuteMs)) } };
}

/** Повний рядок черги або `null`, коли позиції немає (тоді лишається звичайний текст). */
export function queueText(info: QueueInfo, now: number = Date.now()): string | null {
  const position = info.queuePosition;
  if (position === null || position === undefined) return null;
  const head = t('jobs.queuePosition', { position });
  const eta = queueEtaOf(info.estimatedStartAt, now);
  if (eta === null) return head;
  const tail = 'params' in eta ? t('jobs.queueEta', eta.params) : t('jobs.queueEtaSoon');
  return `${head}. ${tail}`;
}

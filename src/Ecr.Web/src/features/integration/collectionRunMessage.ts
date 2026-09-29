import { t } from '@/shared/i18n';

/**
 * Текст причини прогону збору (`itg.CollectionRun.ErrorMessage`) мовою
 * інтерфейсу (аудит U12).
 *
 * ⛔ Збирач пише причину КОНВЕРТОМ — ключ каталогу, параметри й вкладена
 * причина (`JobProgressMessageEnvelope`, `Q-326`: `{"k":…,"p":{…},"i":{…}}`),
 * — бо в момент запису мова читача невідома. Показати його сирим означало б
 * JSON на екрані; показати готове речення сервера — українську на англійському
 * інтерфейсі (саме так було до U12).
 *
 * ⚠ Рядок, що НЕ є конвертом (прогони до U12, відмова в автентифікації,
 * покинутий прогін), повертається як є: це вже текст, і іншого в базі немає.
 * Так само — конверт із ключем, якого цей файл не знає (новіший сервер): сирий
 * рядок чесніший за вгаданий переклад.
 *
 * ⚠ Ключі — ЛІТЕРАЛАМИ в `render`, а не `t(envelope.k)`: так сторож
 * `EndpointCoverageTests` бачить кожен і звіряє його з `09-seed.sql`.
 * `{message}` — зарезервоване ім'я вкладеної причини, як і в серверному
 * `JobProgressMessageResolver`.
 */
export function collectionRunErrorText(raw: string): string {
  const envelope = decode(raw);
  if (envelope === null) return raw;

  return resolve(envelope) ?? raw;
}

interface Envelope {
  readonly k: string;
  readonly p?: Record<string, string>;
  readonly i?: Envelope;
}

function decode(raw: string): Envelope | null {
  if (!raw.startsWith('{')) return null;

  try {
    const parsed: unknown = JSON.parse(raw);
    return isEnvelope(parsed) ? parsed : null;
  } catch {
    return null;
  }
}

function isEnvelope(value: unknown): value is Envelope {
  if (typeof value !== 'object' || value === null) return false;

  const candidate = value as { k?: unknown; p?: unknown; i?: unknown };
  if (typeof candidate.k !== 'string' || candidate.k.length === 0) return false;
  if (candidate.p !== undefined && (typeof candidate.p !== 'object' || candidate.p === null)) return false;

  return candidate.i === undefined || isEnvelope(candidate.i);
}

function resolve(envelope: Envelope): string | null {
  const params: Record<string, string> = { ...envelope.p };

  if (envelope.i !== undefined) {
    const inner = resolve(envelope.i);
    if (inner === null) return null;
    params.message = inner;
  }

  return render(envelope.k, params);
}

function render(key: string, params: Record<string, string>): string | null {
  switch (key) {
    case 'jobs.collectionRunReason':
      return t('jobs.collectionRunReason', params);
    case 'jobs.collectionSourceUnavailable':
      return t('jobs.collectionSourceUnavailable', params);
    case 'jobs.collectionSourceError':
      return t('jobs.collectionSourceError', params);
    case 'jobs.collectionSameTimestamp':
      return t('jobs.collectionSameTimestamp', params);
    case 'jobs.collectionPageLimit':
      return t('jobs.collectionPageLimit', params);
    case 'jobs.collectionUnitChanged':
      return t('jobs.collectionUnitChanged', params);
    case 'jobs.collectionTimeout':
      return t('jobs.collectionTimeout', params);
    case 'jobs.collectionCancelled':
      return t('jobs.collectionCancelled', params);
    case 'jobs.collectionRuleFailed':
      return t('jobs.collectionRuleFailed', params);
    case 'jobs.collectionRunFailed':
      return t('jobs.collectionRunFailed', params);
    case 'jobs.collectionCloseFailed':
      return t('jobs.collectionCloseFailed', params);
    case 'jobs.collectionAuthRefused':
      return t('jobs.collectionAuthRefused', params);
    case 'jobs.collectionAbandoned':
      return t('jobs.collectionAbandoned', params);
    default:
      return null;
  }
}

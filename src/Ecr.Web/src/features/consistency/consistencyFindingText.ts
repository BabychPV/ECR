import { t } from '@/shared/i18n';

/** Текст знахідки для показу та ознака, чи він перекладений каталогом. */
export interface FindingText {
  /** Що показати. */
  readonly text: string;

  /**
   * `true` — текст з каталогу мовою інтерфейсу; `false` — сирий текст, як його записала задача (знахідки до
   * перекладу, невідомий ключ): лише під такими екран лишає підпис «текст не перекладається».
   */
  readonly translated: boolean;
}

/**
 * Текст знахідки перевірки узгодженості (`aud.ConsistencyIssue.Message`) мовою інтерфейсу.
 *
 * ⛔ Аудит live 12.11: англійський інтерфейс показував українські службові речення задачі («Рішення людини 28.10…»).
 * Тепер `ConsistencyCheckJob` пише КОНВЕРТ `{"k":"consistency.finding.<RULE>","p":{…}}` (той самий, що й у
 * `itg.JobProgress.Message`), а переклад — тут.
 *
 * ⚠ Рядок, що НЕ є конвертом (знахідки, записані до цього, і ті, що пише процедура архівації), повертається як є —
 * іншого тексту в базі немає. Так само конверт із ключем, якого цей файл не знає: сирий рядок чесніший за вгаданий
 * переклад.
 *
 * ⚠ Ключі — ЛІТЕРАЛАМИ, а не `t(envelope.k)`: так сторож `EndpointCoverageTests` бачить кожен і звіряє його з сідом.
 */
export function consistencyFindingText(raw: string): FindingText {
  const envelope = decode(raw);
  const text = envelope === null ? null : render(envelope.k, { ...envelope.p });

  return text === null ? { text: raw, translated: false } : { text, translated: true };
}

interface Envelope {
  readonly k: string;
  readonly p?: Record<string, string>;
}

function decode(raw: string): Envelope | null {
  if (!raw.startsWith('{')) return null;

  try {
    const parsed: unknown = JSON.parse(raw);
    if (typeof parsed !== 'object' || parsed === null) return null;

    const candidate = parsed as { k?: unknown; p?: unknown };
    if (typeof candidate.k !== 'string' || candidate.k.length === 0) return null;
    if (candidate.p !== undefined && (typeof candidate.p !== 'object' || candidate.p === null)) return null;

    return candidate as Envelope;
  } catch {
    return null;
  }
}

function render(key: string, params: Record<string, string>): string | null {
  switch (key) {
    case 'consistency.finding.ORPHANED_CELL':
      return t('consistency.finding.ORPHANED_CELL', params);
    case 'consistency.finding.BROKEN_FK':
      return t('consistency.finding.BROKEN_FK', params);
    case 'consistency.finding.ARCHIVE_CHECKSUM':
      return t('consistency.finding.ARCHIVE_CHECKSUM', params);
    case 'consistency.finding.UNBOUND_CALCULATED_COLUMN':
      return t('consistency.finding.UNBOUND_CALCULATED_COLUMN', params);
    case 'consistency.finding.UNSOURCED_FORMULA_COLUMN':
      return t('consistency.finding.UNSOURCED_FORMULA_COLUMN', params);
    case 'consistency.finding.NON_CURRENT_RUN_ROWS':
      return t('consistency.finding.NON_CURRENT_RUN_ROWS', params);
    default:
      return null;
  }
}

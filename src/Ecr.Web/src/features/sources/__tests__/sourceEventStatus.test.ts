import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { SourceEventStatuses, statusColor, statusExplanation, statusLabel } from '../sourceEventStatus';

/**
 * Вісім станів зв'язку події (`SourceEventLinkStatus`): кожен має назву й пояснення в каталозі всіма трьома
 * мовами продукту — інакше бейдж у таблиці подій показав би позначений ключ.
 */
describe('sourceEventStatus', () => {
  const seed = readFileSync(path.resolve(process.cwd(), '../Ecr.Infrastructure/Persistence/Sql/09-seed.sql'), 'utf8');

  it('вісім станів — рівно ті, що в контракті', () => {
    expect(SourceEventStatuses).toEqual([
      'Synced',
      'Open',
      'Missing',
      'PeriodClosed',
      'PeriodChanged',
      'PeriodNotOpen',
      'Unmapped',
      'RowLimit',
    ]);
  });

  it.each(SourceEventStatuses)('%s: назва й пояснення — власні ключі, у сіді en/ru/kz', (status) => {
    expect(statusLabel(status)).toBe(`⟦sourceEvents.status.${status}⟧`);
    expect(statusExplanation(status)).toBe(`⟦sourceEvents.statusHint.${status}⟧`);

    for (const key of [`sourceEvents.status.${status}`, `sourceEvents.statusHint.${status}`]) {
      for (const lang of ['en', 'ru', 'kz']) {
        expect(seed, `${key} (${lang})`).toMatch(new RegExp(`\\(N'${key.replace(/\./g, '\\.')}',\\s*N'${lang}'`));
      }
    }
  });

  it('кольори — лише токени теми й нейтральний сірий; стан, що просить дії, не сірий', () => {
    const allowed = new Set(['statusSuccess', 'statusWarning', 'statusError', 'gray']);
    for (const status of SourceEventStatuses) expect(allowed.has(statusColor(status))).toBe(true);

    expect(statusColor('Synced')).toBe('statusSuccess');
    expect(statusColor('Missing')).toBe('statusWarning');
    expect(statusColor('Unmapped')).toBe('statusWarning');
    expect(statusColor('RowLimit')).toBe('statusError');
  });
});

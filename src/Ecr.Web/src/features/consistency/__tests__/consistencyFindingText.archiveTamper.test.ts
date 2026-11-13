import { describe, it, expect } from 'vitest';
import { consistencyFindingText } from '@/features/consistency/consistencyFindingText';

/**
 * B70: знахідка прямої правки архіву (`ARCHIVE_CHECKSUM`, сутність `arc.Period`) приходить конвертом із ключем
 * `consistency.finding.ARCHIVE_TAMPER`. Каталог у тесті порожній, тож `t()` повертає позначку `⟦ключ (параметри)⟧`.
 * Мутація: прибрати гілку `ARCHIVE_TAMPER` зі `switch` — повертається сирий JSON (`translated: false`), червоний.
 */
describe('consistencyFindingText: ARCHIVE_TAMPER', () => {
  it('перекладає конверт прямої правки архіву, а не показує сирий JSON', () => {
    const raw = JSON.stringify({
      k: 'consistency.finding.ARCHIVE_TAMPER',
      p: { period: '202706', runId: '15', cellsThen: '2', cellsNow: '1' },
    });

    const result = consistencyFindingText(raw);

    expect(result.translated).toBe(true);
    expect(result.text).toBe('⟦consistency.finding.ARCHIVE_TAMPER (period=202706, runId=15, cellsThen=2, cellsNow=1)⟧');
  });
});

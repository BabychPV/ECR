import { describe, it, expect } from 'vitest';
import { consistencyFindingText } from '@/features/consistency/consistencyFindingText';

/**
 * ✎ B80 N4: знахідка `STALE_APPROVED_DEPENDENT` (затверджений місяць споживача, чиє джерело змінилося) приходить
 * конвертом `consistency.finding.STALE_APPROVED_DEPENDENT`; сід має один текст на обидві причини. Каталог у тесті
 * порожній, тож `t()` повертає позначку `⟦ключ (параметри)⟧`.
 * Мутація: прибрати гілку зі `switch` — сирий JSON (`translated: false`), червоний.
 */
describe('consistencyFindingText: STALE_APPROVED_DEPENDENT', () => {
  it.each(['notApproved', 'reapproved'])('перекладає конверт (reason=%s), а не показує сирий JSON', (reason) => {
    const raw = JSON.stringify({
      k: 'consistency.finding.STALE_APPROVED_DEPENDENT',
      p: { documentId: '96', periodKey: '202603', sourceDocumentId: '412', sourcePeriodKey: '202603', reason },
    });

    const result = consistencyFindingText(raw);

    expect(result.translated).toBe(true);
    expect(result.text).toBe(
      `⟦consistency.finding.STALE_APPROVED_DEPENDENT (documentId=96, periodKey=202603, sourceDocumentId=412, sourcePeriodKey=202603, reason=${reason})⟧`,
    );
  });
});

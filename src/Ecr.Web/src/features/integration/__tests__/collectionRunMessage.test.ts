import { describe, expect, it } from 'vitest';
import { collectionRunErrorText } from '@/features/integration/collectionRunMessage';

/**
 * U12: розбір причини прогону збору. Каталог у тесті порожній, тож `t()`
 * повертає позначку `⟦ключ (параметри)⟧` — саме по ній і видно, що рядок
 * розібрано як конверт, а не показано сирим.
 */
describe('collectionRunErrorText', () => {
  it('конверт із вкладеною причиною резолвиться через каталог, {message} — вкладена', () => {
    const raw = JSON.stringify({
      k: 'jobs.collectionRunReason',
      p: { code: 'ECR-INT-0503' },
      i: { k: 'jobs.collectionSourceUnavailable' },
    });

    expect(collectionRunErrorText(raw)).toBe(
      '⟦jobs.collectionRunReason (code=ECR-INT-0503, message=⟦jobs.collectionSourceUnavailable ()⟧)⟧',
    );
  });

  it('відмова в автентифікації і покинутий прогін — теж через каталог', () => {
    expect(
      collectionRunErrorText(
        JSON.stringify({ k: 'jobs.collectionAuthRefused', p: { sourceCode: 'STACK-1', detail: '' } }),
      ),
    ).toBe('⟦jobs.collectionAuthRefused (sourceCode=STACK-1, detail=)⟧');
    expect(collectionRunErrorText(JSON.stringify({ k: 'jobs.collectionAbandoned' }))).toBe(
      '⟦jobs.collectionAbandoned ()⟧',
    );
  });

  it('старий рядок (сирий текст до U12) — як є', () => {
    expect(collectionRunErrorText('ECR-INT-0503: джерело недоступне')).toBe(
      'ECR-INT-0503: джерело недоступне',
    );
  });

  it('невідомий ключ або не-конверт у JSON — сирий рядок, а не вгаданий переклад', () => {
    const unknown = JSON.stringify({ k: 'jobs.somethingNewer', p: { a: '1' } });
    expect(collectionRunErrorText(unknown)).toBe(unknown);

    const unknownInner = JSON.stringify({ k: 'jobs.collectionRunReason', i: { k: 'jobs.somethingNewer' } });
    expect(collectionRunErrorText(unknownInner)).toBe(unknownInner);

    expect(collectionRunErrorText('{not json')).toBe('{not json');
    expect(collectionRunErrorText('{"x":1}')).toBe('{"x":1}');
  });
});

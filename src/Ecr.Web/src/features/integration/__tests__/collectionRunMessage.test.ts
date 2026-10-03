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

  it('власна відмова адаптера з кодом каталогу резолвиться вкладеним ключем із параметрами', () => {
    // ⛔ До фіксу збирач писав сюди `ECR-INT-0503` + `jobs.collectionSourceError`
    // з українським реченням адаптера. Тепер — код і ключ самої відмови; без
    // гілки в `render` цей ключ дав би `null`, і вся причина лишилася б сирим JSON.
    const raw = JSON.stringify({
      k: 'jobs.collectionRunReason',
      p: { code: 'ECR-INT-0422' },
      i: {
        k: 'err.ECR-INT-0422.timestampUnreadable',
        p: { dataSource: 'PIAF', sourcePath: 'tagA', valueType: 'NULL' },
      },
    });

    expect(collectionRunErrorText(raw)).toBe(
      '⟦jobs.collectionRunReason (code=ECR-INT-0422, message=⟦err.ECR-INT-0422.timestampUnreadable (dataSource=PIAF, sourcePath=tagA, valueType=NULL)⟧)⟧',
    );
  });

  it('подія `SourceDataRefused` — два рівні вкладення, кожен через каталог', () => {
    const raw = JSON.stringify({
      k: 'coverageEvents.sourceDataRefused',
      p: { path: 'tagA', from: 'F', to: 'T', key: 'ABC' },
      i: {
        k: 'jobs.collectionRunReason',
        p: { code: 'ECR-INT-0422' },
        i: { k: 'err.ECR-INT-0422.timestampUnreadable', p: { sourcePath: 'tagA' } },
      },
    });

    expect(collectionRunErrorText(raw)).toBe(
      '⟦coverageEvents.sourceDataRefused (path=tagA, from=F, to=T, key=ABC, message=⟦jobs.collectionRunReason (code=ECR-INT-0422, message=⟦err.ECR-INT-0422.timestampUnreadable (sourcePath=tagA)⟧)⟧)⟧',
    );
  });

  it.each([
    ['coverageEvents.periodClosed', { state: 'Closed' }, 'state=Closed'],
    ['coverageEvents.periodMissing', undefined, ''],
    ['coverageEvents.pointCeiling', { field: 'F1', limit: '5000' }, 'field=F1, limit=5000'],
    ['coverageEvents.keptManual', { cell: 'r1:c2' }, 'cell=r1:c2'],
    ['coverageEvents.writeConflict', { cell: 'r1:c2' }, 'cell=r1:c2'],
    ['coverageEvents.needsConfirmation', { cell: 'r1:c2' }, 'cell=r1:c2'],
    ['coverageEvents.eventWriteFailed', { eventId: '7', reason: 'нема доступу' }, 'eventId=7, reason=нема доступу'],
    ['coverageEvents.eventWritePartial', { eventId: '7', rowKey: 'R1' }, 'eventId=7, rowKey=R1'],
    ['coverageEvents.eventRowNotCreated', { eventId: '7', rowKey: 'R1' }, 'eventId=7, rowKey=R1'],
    ['coverageEvents.eventRemovalSourceEmpty', { count: '3' }, 'count=3'],
    ['coverageEvents.eventTemplateOverlap', { eventId: '7', template: 'A', other: 'B' }, 'eventId=7, template=A, other=B'],
    ['coverageEvents.eventRemovalKeptManual', { eventId: '7', rowKey: 'R1' }, 'eventId=7, rowKey=R1'],
    ['coverageEvents.eventRemovalLimit', { count: '30', limit: '10', linked: '40' }, 'count=30, limit=10, linked=40'],
    [
      'coverageEvents.eventRemovalSheetSubmitted',
      { eventId: '7', rowKey: 'R1', status: 'Submitted' },
      'eventId=7, rowKey=R1, status=Submitted',
    ],
    // L3-05: адаптер Sql відмовив політикою адреси до з'єднання.
    ['err.ECR-INT-0503.endpointForbidden', { dataSource: 'FLERT' }, 'dataSource=FLERT'],
  ])('подія покриття від задачі `%s` резолвиться через каталог, а не лишається JSON', (k, p, shown) => {
    // Без гілки в `render` ключ дав би `null` і в таблиці лишився б сирий JSON.
    const raw = JSON.stringify(p === undefined ? { k } : { k, p });

    expect(collectionRunErrorText(raw)).toBe(`⟦${k} (${shown})⟧`);
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

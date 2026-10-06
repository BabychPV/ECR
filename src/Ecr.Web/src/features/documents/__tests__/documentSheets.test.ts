import { describe, expect, it } from 'vitest';
import { documentState, hasSheetStates, sheetLabels } from '@/features/documents/documentSheets';

/**
 * Стан документа в переліку — дзеркало серверного правила
 * (`DocumentListSummaryStore`): найгірший зі станів аркушів,
 * `Rejected > Draft > Submitted > Approved`. Інакше клік «2 Draft» у смузі
 * лічильників показав би рядки з іншим станом у колонці.
 */
const of = (states: Record<string, string>) => ({ sheetStates: states });

describe('documentState', () => {
  it.each([
    [{ A: 'Approved', B: 'Rejected', C: 'Draft' }, 'Rejected'],
    [{ A: 'Approved', B: 'Submitted', C: 'Draft' }, 'Draft'],
    [{ A: 'Approved', B: 'Submitted' }, 'Submitted'],
    [{ A: 'Approved', B: 'Approved' }, 'Approved'],
  ])('%o → %s', (states, expected) => {
    expect(documentState(of(states))).toBe(expected);
  });

  it('без станів (немає періоду) — null, а не «Draft»', () => {
    expect(documentState(of({}))).toBeNull();
    expect(hasSheetStates(of({}))).toBe(false);
  });

  it('невідомий стан повертається як є — бейдж позначить його «увагою»', () => {
    expect(documentState(of({ A: 'Approved', B: 'Returned' }))).toBe('Returned');
  });
});

describe('sheetLabels', () => {
  it('порядок і назви — з `sheets`; без назви — код', () => {
    expect(
      sheetLabels({
        sheetStates: { WTR: 'Rejected', GEN: 'Draft' },
        sheets: [
          { code: 'GEN', nameL10n: { values: { en: 'General info' } }, state: 'Draft' },
          { code: 'WTR', nameL10n: { values: {} }, state: 'Rejected' },
        ],
      }),
    ).toEqual([
      { code: 'GEN', label: 'General info', state: 'Draft' },
      { code: 'WTR', label: 'WTR', state: 'Rejected' },
    ]);
  });

  it('без `sheets` — пари словника з кодом', () => {
    expect(sheetLabels({ sheetStates: { S7: 'Draft' } })).toEqual([{ code: 'S7', label: 'S7', state: 'Draft' }]);
  });
});

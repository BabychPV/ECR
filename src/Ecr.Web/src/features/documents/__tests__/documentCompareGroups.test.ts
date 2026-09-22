import { describe, expect, it } from 'vitest';
import {
  compareCellText,
  groupCompareByTable,
  isCompareEmpty,
} from '@/features/documents/documentCompareGroups';
import {
  CurrentState,
  documentCompareUrl,
  documentVersionsUrl,
  type DocumentCompare,
} from '@/features/documents/documentVersionsApi';

/**
 * Розкладка різниці версій по таблицях і показ значень (`ФВ-5.22`).
 *
 * ⛔ Перевіряється саме ЧИСТА функція, а не рендер: усі чотири твердження, які
 * тут доводяться (три переліки не зливаються, таблиця з самими лише доданими
 * рядками не зникає, «однакові» рахує всі три переліки, значення не
 * форматується), не потребують DOM — а через нього коштували б секунди на кожне.
 */
function compare(patch: Partial<DocumentCompare>): DocumentCompare {
  return {
    documentId: 7,
    periodKey: 202601,
    fromVersionId: 11,
    toVersionId: null,
    changes: [],
    addedRows: [],
    removedRows: [],
    truncated: false,
    ...patch,
  };
}

describe('groupCompareByTable: різниця по таблицях', () => {
  it('зміни, додані й видалені рядки лягають у РІЗНІ переліки тієї самої таблиці', () => {
    const groups = groupCompareByTable(
      compare({
        changes: [
          { tableCode: 'T1', rowKey: 'r1', columnCode: 'C1', oldValue: '1', newValue: '2' },
        ],
        addedRows: [{ rowId: 5, tableCode: 'T1', rowKey: 'r9' }],
        removedRows: [{ rowId: 6, tableCode: 'T1', rowKey: 'r0' }],
      }),
    );

    expect(groups).toHaveLength(1);
    expect(groups[0]?.tableCode).toBe('T1');

    /*
     * ⛔ Саме три окремі твердження про ДОВЖИНУ кожного переліку. Мутація, що
     * зіллє додані рядки в `changes` (найприродніша спроба «спростити»), лишила
     * б зеленим будь-яку перевірку виду «щось про T1 є».
     */
    expect(groups[0]?.changes.map((c) => c.rowKey)).toEqual(['r1']);
    expect(groups[0]?.addedRows.map((r) => r.rowKey)).toEqual(['r9']);
    expect(groups[0]?.removedRows.map((r) => r.rowKey)).toEqual(['r0']);
  });

  it('таблиця, у якій є лише додані рядки, у переліку залишається', () => {
    const groups = groupCompareByTable(
      compare({
        changes: [
          { tableCode: 'T1', rowKey: 'r1', columnCode: 'C1', oldValue: '1', newValue: '2' },
        ],
        addedRows: [{ rowId: 5, tableCode: 'T2', rowKey: 'r9' }],
      }),
    );

    // ⚠ Порядок — за кодом таблиці: інакше та сама таблиця стояла б у різних
    // місцях залежно від того, у якому з трьох переліків вона трапилася першою.
    expect(groups.map((g) => g.tableCode)).toEqual(['T1', 'T2']);
    expect(groups[1]?.changes).toHaveLength(0);
    expect(groups[1]?.addedRows).toHaveLength(1);
  });

  it('порядок записів усередині таблиці — як прийшов із сервера', () => {
    const groups = groupCompareByTable(
      compare({
        changes: [
          { tableCode: 'T1', rowKey: 'rZ', columnCode: 'C1', oldValue: null, newValue: '2' },
          { tableCode: 'T1', rowKey: 'rA', columnCode: 'C1', oldValue: null, newValue: '3' },
        ],
      }),
    );

    expect(groups[0]?.changes.map((c) => c.rowKey)).toEqual(['rZ', 'rA']);
  });
});

describe('isCompareEmpty: «версії однакові» — це всі ТРИ переліки', () => {
  it('усе порожнє — однакові', () => {
    expect(isCompareEmpty(compare({}))).toBe(true);
  });

  /*
   * ⛔ Три окремі випадки, бо мутація «дивитись лише на changes» — саме та, що
   * напрошується: версія, у якій рядок зник, а жодне число не змінилося,
   * оголошувалася б однаковою, і людина не дізналася б про втрачений рядок.
   */
  it.each([
    [
      'змінена комірка',
      compare({
        changes: [
          { tableCode: 'T1', rowKey: 'r1', columnCode: 'C1', oldValue: '1', newValue: '2' },
        ],
      }),
    ],
    ['доданий рядок', compare({ addedRows: [{ rowId: 5, tableCode: 'T1', rowKey: 'r9' }] })],
    ['видалений рядок', compare({ removedRows: [{ rowId: 6, tableCode: 'T1', rowKey: 'r0' }] })],
  ])('%s — версії НЕ однакові', (_name, value) => {
    expect(isCompareEmpty(value)).toBe(false);
  });
});

describe('compareCellText: значення показується як прийшло', () => {
  /*
   * ⛔ Головне твердження файлу. Сервер порівнює комірки як `decimal`
   * (`1.5` = `1.50`) і віддає рядки саме тому, що `decimal(28,16)` не
   * вміщається в IEEE-754. Будь-яке приведення на клієнті — `Number(value)`,
   * `formatDecimal`, обрізання хвостових нулів — показало б масштаб, якого в
   * документі немає.
   */
  it.each([
    ['1.50', '1.50'],
    ['1.5000000000000000', '1.5000000000000000'],
    ['0.10', '0.10'],
    ['007', '007'],
    ['-0.0', '-0.0'],
  ])('«%s» лишається «%s»', (value, expected) => {
    expect(compareCellText(value)).toBe(expected);
  });

  it('відсутнє й порожнє значення — одне й те саме тире', () => {
    expect(compareCellText(null)).toBe('—');
    expect(compareCellText('')).toBe('—');
  });
});

describe('Адреси запитів — рівно за контрактом', () => {
  /*
   * ⛔ Клієнт не додає НІЧОГО понад контракт. Стелю переліку (200 версій) і
   * стелю кожного переліку різниці (1000 записів) ставить сервер; параметр,
   * доданий тут «щоб не тягнути зайвого», змінив би поведінку, описану
   * контрактом, і помітити це можна було б лише на сервері.
   *
   * ⚠ Перевіряється ПОБУКВЕНО, а не `toContain`: зайвий `&limit=200` пройшов би
   * будь-яку перевірку на входження.
   */
  it('перелік версій: єдиний параметр — periodKey', () => {
    expect(documentVersionsUrl(7, 202601)).toBe('/api/v1/documents/7/versions?periodKey=202601');
  });

  it('порівняння з версією: рівно from і to', () => {
    expect(documentCompareUrl(7, 11, 12)).toBe('/api/v1/documents/7/compare?from=11&to=12');
  });

  it('порівняння з поточним станом: to=current', () => {
    expect(documentCompareUrl(7, 11, CurrentState)).toBe(
      '/api/v1/documents/7/compare?from=11&to=current',
    );
  });
});

import { describe, expect, it } from 'vitest';
import type { ColumnDto, RowDto, TableSliceDto } from '@/api/types';
import { columnTotals, isTotalsRow, TotalsRowKey, totalsRow } from '../gridTotals';

/**
 * Рядок підсумків сітки документа (`UI-08`).
 *
 * ⛔ Чому підсумок рахується на клієнті — у шапці `gridTotals.ts`: контракт
 * зрізу агрегатів не несе взагалі. Тут перевіряється саме те, що з цього
 * рішення випливає: підсумок дорівнює сумі ТОГО, ЩО ВИДНО в сітці, і нічого
 * іншого.
 *
 * ⚠ Модуль чистий — жодного рендера RevoGrid, як і `clipboard.ts`/`undo.ts`.
 */

function column(overrides: Partial<ColumnDto> = {}): ColumnDto {
  return {
    id: 1,
    code: 'C1',
    header: 'Колонка 1',
    dataType: 'Decimal',
    ordinal: 1,
    isReadOnly: false,
    isRequired: false,
    isRequiredByMethodology: false,
    displayFormat: null,
    defaultValue: null,
    lookupRegistryDefId: null,
    unitId: null,
    unitSymbol: null,
    ...overrides,
  };
}

function row(rowKey: string, cells: Record<string, unknown>, label: string | null = null): RowDto {
  return {
    rowKey,
    ordinal: 1,
    rowKind: 'Item',
    label,
    rowVersion: '0x01',
    cells,
    isOrphaned: false,
  };
}

function slice(columns: ColumnDto[], rows: RowDto[]): TableSliceDto {
  return {
    tableInstanceId: 1,
    periodKey: 202609,
    columns,
    rows,
    cellPermissions: {},
    cellConfirmations: {},
  };
}

describe('columnTotals: сума того, що видно', () => {
  it('порожні комірки НЕ рахуються нулями — ні в сумі, ні в кількості', () => {
    /*
     * ⛔ Це головне твердження всього модуля. «Не заповнювали» і «нуль»
     * різняться у звіті регулятора: колонка з двома вимірами з п'яти рядків
     * не має звітувати «5 значень», а сума не має змінитися від того, що
     * три рядки порожні.
     *
     * ⚠ Три різні способи «порожньо» в одному наборі навмисно: ключа немає
     * (`R-B4`), ключ зі значенням `null`, і порожній рядок.
     */
    const totals = columnTotals(
      slice(
        [column({ code: 'C1' })],
        [
          row('r1', { C1: '10' }),
          row('r2', {}),
          row('r3', { C1: null }),
          row('r4', { C1: '' }),
          row('r5', { C1: '2.5' }),
        ],
      ),
    );

    expect(totals.get('C1')).toEqual({ sum: '12.5', count: 2 });
  });

  it('колонка без жодного заповненого значення взагалі не має підсумку — не «0»', () => {
    const totals = columnTotals(slice([column({ code: 'C1' })], [row('r1', {}), row('r2', { C1: null })]));

    // ⛔ `0` у рядку підсумків читається як виміряний нуль — те саме, проти
    // чого стоїть правило вище, лише на рівні колонки.
    expect(totals.has('C1')).toBe(false);
  });

  it('шістнадцятий знак не губиться — сума рядкова, не через `Number`', () => {
    /*
     * ⛔ Мутація, яку це ловить: `values.reduce((a, b) => a + Number(b), 0)`.
     * `Number('1234.1234567890123456')` дає `1234.1234567890124`, тобто
     * відповідь розійдеться вже в тринадцятому знаку — а `decimal(25,16)` тут
     * не екзотика (`NumericPolicy.OutputScale`, `rounding.ts`).
     */
    const totals = columnTotals(
      slice(
        [column({ code: 'C1' })],
        [row('r1', { C1: '1234.1234567890123456' }), row('r2', { C1: '0.0000000000000001' })],
      ),
    );

    expect(totals.get('C1')?.sum).toBe('1234.1234567890123457');
  });

  it('доданки різного масштабу зводяться до найдовшого дробу', () => {
    const totals = columnTotals(
      slice([column({ code: 'C1' })], [row('r1', { C1: '1.5' }), row('r2', { C1: '2.25' }), row('r3', { C1: '3' })]),
    );

    expect(totals.get('C1')?.sum).toBe('6.75');
  });

  it("від'ємні доданки складаються, а нуль лишається `0`, не `-0`", () => {
    const totals = columnTotals(
      slice([column({ code: 'C1' })], [row('r1', { C1: '-2.5' }), row('r2', { C1: '2.5' })]),
    );

    expect(totals.get('C1')).toEqual({ sum: '0', count: 2 });
  });

  it('нечислове значення в числовій колонці НЕ стає нулем — воно просто не бере участі', () => {
    // ⚠ `'н/д'` — рівно те, що `edits.coerce` лишає текстом до відповіді
    // сервера `ECR-CELL-0422`. Мовчазний нуль тут читався б як вимірювання.
    const totals = columnTotals(
      slice([column({ code: 'C1' })], [row('r1', { C1: 'н/д' }), row('r2', { C1: '4' })]),
    );

    expect(totals.get('C1')).toEqual({ sum: '4', count: 1 });
  });

  it('булеве значення не підсумовується: `true` — не одиниця', () => {
    const totals = columnTotals(
      slice([column({ code: 'C1' })], [row('r1', { C1: true }), row('r2', { C1: '7' })]),
    );

    expect(totals.get('C1')).toEqual({ sum: '7', count: 1 });
  });

  it('число JSON-ом (старіший сервер, `Int`) підсумовується так само, як рядок', () => {
    const totals = columnTotals(
      slice([column({ code: 'N1', dataType: 'Int' })], [row('r1', { N1: 3 }), row('r2', { N1: 4 })]),
    );

    expect(totals.get('N1')).toEqual({ sum: '7', count: 2 });
  });

  it('підсумовуються `Decimal`, `Int`, `Formula`, `Calculated` — і жоден інший тип', () => {
    const totals = columnTotals(
      slice(
        [
          column({ id: 1, code: 'DEC', dataType: 'Decimal' }),
          column({ id: 2, code: 'INT', dataType: 'Int' }),
          column({ id: 3, code: 'FRM', dataType: 'Formula' }),
          column({ id: 4, code: 'CLC', dataType: 'Calculated' }),
          column({ id: 5, code: 'STR', dataType: 'String' }),
          column({ id: 6, code: 'DAT', dataType: 'Date' }),
          column({ id: 7, code: 'BOO', dataType: 'Bool' }),

          // ⛔ `Lookup` тримає `ValueRegistryEntryId` — число, сума якого не
          // означає нічого, а виглядає як вимірювання.
          column({ id: 8, code: 'LKP', dataType: 'Lookup' }),
        ],
        [row('r1', { DEC: '1', INT: 2, FRM: '3', CLC: '4', STR: '5', DAT: '6', BOO: '7', LKP: 8 })],
      ),
    );

    expect([...totals.keys()].sort()).toEqual(['CLC', 'DEC', 'FRM', 'INT']);
  });

  it('`defaultValue` колонки входить у підсумок — бо його ВИДНО в комірці (ФВ-3.8)', () => {
    /*
     * ⚠ Правило одне: сума — це сума видимого. `gridRows` малює порожню
     * комірку значенням `defaultValue`, тож підсумок, який його пропускає,
     * показував би суму чисел, яких на екрані немає.
     */
    const totals = columnTotals(
      slice([column({ code: 'C1', defaultValue: '5' })], [row('r1', {}), row('r2', { C1: '1' })]),
    );

    expect(totals.get('C1')).toEqual({ sum: '6', count: 2 });
  });

  it('незбережена правка перекриває збережене значення', () => {
    // ⚠ Без цього шару підсумок відставав би від екрана на все вікно
    // автозбереження — 500 мс тиші плюс час запиту.
    const totals = columnTotals(slice([column({ code: 'C1' })], [row('r1', { C1: '1' }), row('r2', { C1: '1' })]), {
      pending: new Map([['r1:C1', { rowKey: 'r1', columnCode: 'C1', value: '100' }]]),
    });

    expect(totals.get('C1')).toEqual({ sum: '101', count: 2 });
  });

  it('незбережене СТИРАННЯ прибирає комірку з підсумку', () => {
    const totals = columnTotals(slice([column({ code: 'C1' })], [row('r1', { C1: '1' }), row('r2', { C1: '9' })]), {
      pending: new Map([['r1:C1', { rowKey: 'r1', columnCode: 'C1', value: null }]]),
    });

    expect(totals.get('C1')).toEqual({ sum: '9', count: 1 });
  });

  it('підтверджене значення (`ФВ-2.16`) теж видно — і теж рахується', () => {
    const totals = columnTotals(slice([column({ code: 'C1' })], [row('r1', { C1: '1' })]), {
      overrides: new Map<string, unknown>([['r1:C1', '42']]),
    });

    expect(totals.get('C1')).toEqual({ sum: '42', count: 1 });
  });

  it('рядок, якого в зрізі немає, у підсумок не потрапляє — навіть якщо правка на нього є', () => {
    /*
     * ⛔ Аналог вимоги «рядок підсумків не рахує невидимих рядків». Фільтра
     * рядків сітка документа не має (склад рядків задає зріз), тож видимість
     * — це і є «рядок є у зрізі». Мутація, яку це ловить: обхід по ключах
     * `pending` замість обходу `slice.rows` — тоді правка, що лишилася від
     * уже перечитаного зрізу, додавала б число до суми таблиці, у якій цього
     * рядка більше немає.
     */
    const totals = columnTotals(slice([column({ code: 'C1' })], [row('r1', { C1: '1' })]), {
      pending: new Map([['r999:C1', { rowKey: 'r999', columnCode: 'C1', value: '1000' }]]),
    });

    expect(totals.get('C1')).toEqual({ sum: '1', count: 1 });
  });
});

describe('totalsRow: модель закріпленого рядка', () => {
  const twoColumns = [column({ id: 1, code: 'NAME', dataType: 'String' }), column({ id: 2, code: 'C1' })];

  it('несе службовий ключ і суми за кодами колонок', () => {
    const data = slice(twoColumns, [row('r1', { C1: '2' })]);
    const model = totalsRow(data, columnTotals(data), 'Total', '__rowLabel');

    expect(model.__rowKey).toBe(TotalsRowKey);
    expect(model['C1']).toBe('2');
  });

  it('колонка без підсумку лишається ПОРОЖНЬОЮ, а не нулем', () => {
    const data = slice(twoColumns, [row('r1', { NAME: 'Паливо' })]);
    const model = totalsRow(data, columnTotals(data), 'Total', '__rowLabel');

    expect(model['C1']).toBe('');
  });

  it('підпис іде в колонку підпису рядків, коли вона є', () => {
    const data = slice(twoColumns, [row('r1', { C1: '2' }, 'Викиди')]);
    const model = totalsRow(data, columnTotals(data), 'Total', '__rowLabel');

    expect(model['__rowLabel']).toBe('Total');

    // ⚠ І НЕ дублюється в першу непідсумовувану колонку: два написи «Total»
    // в одному рядку читалися б як дві різні підсумкові позиції.
    expect(model['NAME']).toBe('');
  });

  it('без колонки підпису — підпис іде в першу непідсумовувану колонку', () => {
    const data = slice(twoColumns, [row('r1', { C1: '2' })]);
    const model = totalsRow(data, columnTotals(data), 'Total', null);

    expect(model['NAME']).toBe('Total');
  });

  it('немає ні колонки підпису, ні непідсумовуваної — рядок лишається самими числами', () => {
    // ⚠ Заводити колонку заради напису означало б посунути всі дані вбік.
    const data = slice([column({ code: 'C1' })], [row('r1', { C1: '2' })]);
    const model = totalsRow(data, columnTotals(data), 'Total', null);

    expect(Object.values(model)).not.toContain('Total');
    expect(model['C1']).toBe('2');
  });
});

describe('isTotalsRow: рядок підсумків відрізняється від рядка документа', () => {
  it('упізнає свій рядок і не впізнає чужий', () => {
    expect(isTotalsRow({ __rowKey: TotalsRowKey })).toBe(true);
    expect(isTotalsRow({ __rowKey: 'r1' })).toBe(false);
    expect(isTotalsRow({})).toBe(false);
    expect(isTotalsRow(null)).toBe(false);
    expect(isTotalsRow(undefined)).toBe(false);
  });
});

import { describe, expect, it } from 'vitest';
import { columnOptions, type ChangedColumn } from '@/features/audit/filterOptions';

const column = (columnDefId: number): ChangedColumn => ({
  columnDefId,
  columnCode: 'M10',
  columnHeaderL10n: { values: { en: 'October' } },
  sheetCode: 'Fuel',
  sheetNameL10n: { values: { en: 'Fuel' } },
  tableCode: 'Oil',
  tableNameL10n: { values: { en: 'Oil' } },
});

// Рядок журналу до переносу проєкту: Id старої версії шаблону, той самий підпис.
const oldRow = { ...column(100), documentId: 96 } as unknown as Parameters<typeof columnOptions>[0] extends readonly (infer T)[] | undefined ? T : never;

describe('columnOptions: згортання за родоводом (AC-14)', () => {
  it('сервер віддав один пункт на логічну колонку — старий Id із завантаженої сторінки не додає другий', () => {
    const options = columnOptions([oldRow], 96, null, null, [column(200)]);
    expect(options).toHaveLength(1);
    expect(options[0]?.value).toBe('200');
  });

  it('поки перелік з сервера їде, колонки беруться з рядків завантаженої сторінки', () => {
    const options = columnOptions([oldRow], 96, null, null, undefined);
    expect(options.map((o) => o.value)).toEqual(['100']);
  });
});

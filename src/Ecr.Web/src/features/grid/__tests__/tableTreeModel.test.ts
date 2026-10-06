import { describe, expect, it } from 'vitest';
import type { DocumentTableDto } from '@/api/types';
import type { TableStatus } from '@/features/documents/api';
import {
  buildTableTree,
  filterTableTree,
  fillStateOf,
  resolveTable,
  sheetProgress,
  tableUrlKey,
} from '../tableTreeModel';

/**
 * Модель дерева таблиць (`UI-22`): стан таблиці, фільтр, ключ в адресі.
 *
 * ⛔ Найдорожча помилка тут — показати стан, якого не знаємо: «порожня» про таблицю без
 * статусу і «заповнена» про таблицю, де людині нічого заповнювати (`R-13`, `A7-28`).
 */
function table(ordinal: number, code = `T${String(ordinal)}`, instance = 100 + ordinal, def = ordinal): DocumentTableDto {
  return {
    allowsDynamicRows: false,
    maxDynamicRows: null,
    sheetCode: 'S1',
    sheetDefId: 1,
    sheetNameL10n: { values: { en: 'Sheet' } },
    sheetOrdinal: 1,
    tableCode: code,
    tableDefId: def,
    tableInstanceId: instance,
    tableNameL10n: { values: { en: `Table ${String(ordinal)}` } },
    tableOrdinal: ordinal,
  };
}

function status(def: number, filled: number, input: number, errors: number | null = null, warnings: number | null = null): TableStatus {
  return { tableDefId: def, sheetCode: 'S1', filledCells: filled, inputCells: input, errorCount: errors, warningCount: warnings };
}

describe('fillStateOf', () => {
  it.each([
    ['без статусу — невідомо', undefined, 'unknown'],
    ['нічого заповнювати — не «заповнена» (R-13)', status(1, 0, 0), 'none'],
    ['жодної комірки', status(1, 0, 10), 'empty'],
    ['частина', status(1, 3, 10), 'partial'],
    ['усе', status(1, 10, 10), 'filled'],
    ['помилка важить більше за заповненість', status(1, 10, 10, 2), 'error'],
    ['нуль помилок — не помилка', status(1, 10, 10, 0, 3), 'filled'],
  ] as const)('%s', (_name, given, expected) => {
    expect(fillStateOf(given)).toBe(expected);
  });
});

describe('buildTableTree', () => {
  it('упорядковує за tableOrdinal і зіставляє статус за tableDefId', () => {
    const items = buildTableTree([table(2), table(1)], [status(1, 5, 5), status(2, 1, 5, 4, 1)]);

    expect(items.map((item) => item.table.tableOrdinal)).toEqual([1, 2]);
    expect(items.map((item) => item.state)).toEqual(['filled', 'error']);
    expect(items[1]).toMatchObject({ errors: 4, warnings: 1, filledCells: 1, inputCells: 5 });
  });

  it('статус ще не прийшов — усі невідомі, лічильників немає', () => {
    const items = buildTableTree([table(1)], undefined);

    expect(items[0]).toMatchObject({ state: 'unknown', errors: null, warnings: null });
  });
});

describe('filterTableTree', () => {
  const items = buildTableTree(
    [table(1, 'AIR'), table(2, 'WATER'), table(3, 'WASTE')],
    [status(1, 0, 1), status(2, 0, 1, 1), status(3, 0, 1)],
  );
  const name = (t: DocumentTableDto): string => t.tableNameL10n.values?.['en'] ?? '';

  it('шукає в назві, коді й номері без урахування регістру', () => {
    expect(filterTableTree(items, { query: 'wa', errorsOnly: false }, name).map((i) => i.table.tableCode)).toEqual(['WATER', 'WASTE']);
    expect(filterTableTree(items, { query: 'table 3', errorsOnly: false }, name).map((i) => i.table.tableCode)).toEqual(['WASTE']);
    expect(filterTableTree(items, { query: ' 1 ', errorsOnly: false }, name).map((i) => i.table.tableCode)).toEqual(['AIR']);
  });

  it('«лише з помилками»', () => {
    expect(filterTableTree(items, { query: '', errorsOnly: true }, name).map((i) => i.table.tableCode)).toEqual(['WATER']);
  });
});

describe('sheetProgress', () => {
  it('рахує лише таблиці, де людині є що заповнити (R-13)', () => {
    const items = buildTableTree(
      [table(1), table(2), table(3)],
      [status(1, 5, 5), status(2, 0, 0, null, 2), status(3, 1, 5, 3)],
    );

    expect(sheetProgress(items)).toEqual({ filled: 1, total: 2, errors: 3, warnings: 2 });
  });
});

describe('прихований аркуш і вузька роль', () => {
  it('статус чужого аркуша з тим самим tableDefId не підміняє статус таблиці', () => {
    const items = buildTableTree([table(1)], [
      { ...status(1, 9, 9, 7, 4), sheetCode: 'HIDDEN' },
      status(1, 1, 5, null, null),
    ]);

    expect(items[0]).toMatchObject({ state: 'partial', errors: null, warnings: null });
  });

  it('лічильники null по всьому аркушу — у підсумку null, не 0', () => {
    const items = buildTableTree([table(1), table(2)], [status(1, 5, 5), status(2, 0, 5)]);

    expect(sheetProgress(items)).toEqual({ filled: 1, total: 2, errors: null, warnings: null });
  });
});

describe('ключ таблиці в адресі', () => {
  it('код таблиці; повтор коду — екземпляр, і обидва ключі відновлюють вибір', () => {
    const tables = [table(1, 'A'), table(2, 'B', 202), table(3, 'B', 203)];

    expect(tableUrlKey(tables[0]!, tables)).toBe('A');
    expect(tableUrlKey(tables[2]!, tables)).toBe('#203');
    expect(resolveTable('A', tables)?.tableInstanceId).toBe(101);
    expect(resolveTable('#203', tables)?.tableInstanceId).toBe(203);
  });

  it('без ключа або з чужим ключем — перша таблиця за порядком шаблону', () => {
    const tables = [table(2), table(1)];

    expect(resolveTable(null, tables)?.tableOrdinal).toBe(1);
    expect(resolveTable('NOPE', tables)?.tableOrdinal).toBe(1);
    expect(resolveTable(null, [])).toBeUndefined();
  });
});

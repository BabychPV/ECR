import { describe, expect, it } from 'vitest';
import type { RegistryBatchResult, RegistryRow } from '../../rows/api';
import {
  batchItems,
  dirtyCount,
  fromServer,
  newRow,
  problemsByRow,
  setCode,
  setValue,
  toggleDelete,
} from '../pendingRows';

/**
 * Незбережені зміни панелі master-detail (`ФВ-8.16`) → один пакет `POST …/entries/batch`.
 */

const Row: RegistryRow = {
  id: 501,
  code: 'E000000501',
  display: 'N2',
  parentEntryId: null,
  validFrom: null,
  validTo: null,
  version: 'v-501',
  values: {
    CASE: { value: '77', display: '370 Summer', unit: null },
    COMPONENT: { value: '3', display: 'Nitrogen', unit: null },
    MOL_PCT: { value: '1.435977', display: null, unit: '%' },
  },
};

describe('batchItems', () => {
  it('нова частина несе батька в полі композиції — без введення ідентифікатора руками', () => {
    let rows = [newRow('n1', { field: 'CASE', parentId: 77 })];
    rows = setValue(rows, 'n1', 'COMPONENT', '3');
    rows = setValue(rows, 'n1', 'MOL_PCT', ' 12.5 ');
    rows = setValue(rows, 'n1', 'NOTE', '   ');

    expect(batchItems(rows)).toEqual([
      {
        clientRowId: 'n1',
        op: 'upsert',
        id: null,
        code: null,
        baseVersion: null,
        values: { CASE: '77', COMPONENT: '3', MOL_PCT: '12.5' },
      },
    ]);
  });

  it('числові поля йдуть інваріантним записом за правилами сервера — як у сітці даних (L9-04)', () => {
    let rows = [newRow('n1', { field: 'CASE', parentId: 77 })];
    rows = setValue(rows, 'n1', 'MOL_PCT', '12,5');
    rows = setValue(rows, 'n1', 'NOTE', '12,5');
    const existing = setValue([fromServer(Row)], 'e501', 'MOL_PCT', '1 234,5');
    const ambiguous = setValue([fromServer(Row)], 'e501', 'MOL_PCT', '1,234');

    expect(batchItems(rows, new Set(['MOL_PCT']))[0]?.values).toEqual({ CASE: '77', MOL_PCT: '12.5', NOTE: '12,5' });
    expect(batchItems(existing, new Set(['MOL_PCT']))[0]?.values).toEqual({ MOL_PCT: '1234.5' });
    // Неоднозначне — як є: сервер назве його `valueAmbiguousSeparator` у рядку пакета.
    expect(batchItems(ambiguous, new Set(['MOL_PCT']))[0]?.values).toEqual({ MOL_PCT: '1,234' });
  });

  it('новий рядок з ручним кодом надсилає код', () => {
    const rows = setCode([newRow('n1', null)], 'n1', ' CH4 ');
    expect(batchItems(rows)[0]).toMatchObject({ code: 'CH4', id: null });
  });

  it('правка наявного — лише змінені поля й baseVersion; очищене — null', () => {
    let rows = [fromServer(Row)];
    expect(batchItems(rows)).toEqual([]);

    rows = setValue(rows, 'e501', 'MOL_PCT', '1.5');
    rows = setValue(rows, 'e501', 'COMPONENT', '');

    expect(batchItems(rows)).toEqual([
      {
        clientRowId: 'e501',
        op: 'upsert',
        id: 501,
        code: null,
        baseVersion: 'v-501',
        values: { COMPONENT: null, MOL_PCT: '1.5' },
      },
    ]);
  });

  it('повернення значення до серверного — вже не зміна', () => {
    let rows = setValue([fromServer(Row)], 'e501', 'MOL_PCT', '2');
    rows = setValue(rows, 'e501', 'MOL_PCT', '1.435977');
    expect(dirtyCount(rows)).toBe(0);
  });

  it('видалення наявного — op delete з baseVersion; нового — рядок просто зникає', () => {
    let rows = [fromServer(Row), newRow('n1', null)];
    rows = toggleDelete(rows, 'e501');
    rows = toggleDelete(rows, 'n1');

    expect(rows).toHaveLength(1);
    expect(batchItems(rows)).toEqual([
      { clientRowId: 'e501', op: 'delete', id: 501, code: null, baseVersion: 'v-501', values: null },
    ]);
    expect(dirtyCount(toggleDelete(rows, 'e501'))).toBe(0);
  });

  it('код наявного рядка не змінюється', () => {
    expect(setCode([fromServer(Row)], 'e501', 'X')[0]?.code).toBe('E000000501');
  });
});

describe('problemsByRow', () => {
  it('помилки звіту — за ключем рядка; рядки без помилок не потрапляють', () => {
    const result: RegistryBatchResult = {
      added: 0,
      applied: false,
      deleted: 0,
      dryRun: true,
      unchanged: 0,
      updated: 0,
      rows: [
        { clientRowId: 'n1', entryId: null, status: 'error', version: null, errors: [
          { errorCode: 'ECR-REG-4092', field: 'COMPONENT', messageKey: 'err.ECR-REG-4092.keyTaken', params: { code: 'E1' } },
        ] },
        { clientRowId: 'e501', entryId: 501, status: 'updated', version: null, errors: [] },
      ],
    };

    const problems = problemsByRow(result);
    expect([...problems.keys()]).toEqual(['n1']);
    expect(problems.get('n1')).toEqual([
      { field: 'COMPONENT', messageKey: 'err.ECR-REG-4092.keyTaken', params: { code: 'E1' } },
    ]);
    expect(problemsByRow(null).size).toBe(0);
  });
});

import { describe, expect, it } from 'vitest';
import { QueryClient } from '@tanstack/react-query';
import type { PatchCellsRequest, PatchCellsResponse, TableSliceDto } from '@/api/types';
import { queryKeys } from '@/api/queryKeys';
import { applyPatchToSlice } from '../sliceApply';
import { applyPatchLocally } from '../useCellPatch';

/**
 * `G1-01`: `GET` зрізу, що почався ДО коміту `PATCH`, не має права перезаписати
 * локально застосовану відповідь (значення й нову версію рядка).
 *
 * ⚠ Детерміновано: `queryFn` повертає проміси, які тест розв'язує сам, у
 * потрібному порядку — «старе читання» приходить ПІСЛЯ `applyPatchLocally`.
 */

const TableInstanceId = 700;
const PeriodKey = 202609;
const key = queryKeys.slices.one(TableInstanceId, PeriodKey);

function slice(value: number, version: string): TableSliceDto {
  return {
    tableInstanceId: TableInstanceId,
    periodKey: PeriodKey,
    columns: [],
    rows: [
      {
        rowKey: 'R2',
        ordinal: 1,
        rowKind: 'Static',
        label: null,
        rowVersion: version,
        cells: { C1: value },
        isOrphaned: false,
      },
    ],
    cellPermissions: {},
    cellConfirmations: {},
  } as TableSliceDto;
}

const request: PatchCellsRequest = {
  origin: 'UserEdit',
  tableInstanceId: TableInstanceId,
  periodKey: PeriodKey,
  rows: [{ rowKey: 'R2', baseVersion: 'v1', cells: [{ columnCode: 'C1', isEmpty: false, value: '7' }] }],
};

const response: PatchCellsResponse = { appliedCells: 1, rowVersions: { R2: 'v2' }, validation: [] };

async function settle(): Promise<void> {
  for (let i = 0; i < 5; i += 1) await Promise.resolve();
  await new Promise((resolve) => setTimeout(resolve, 0));
}

describe('applyPatchLocally — GET зрізу, що летів до коміту PATCH (G1-01)', () => {
  it('старе читання не перезаписує застосований PATCH; зріз перечитується після коміту', async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const before = slice(1, 'v1');
    client.setQueryData(key, before);

    const gates: Array<(value: TableSliceDto) => void> = [];
    const queryFn = (): Promise<TableSliceDto> =>
      new Promise<TableSliceDto>((resolve) => {
        gates.push(resolve);
      });

    // Перезапит після перерахунку: почався ДО коміту PATCH.
    void client.prefetchQuery({ queryKey: key, queryFn, staleTime: 0 });
    expect(client.getQueryState(key)?.fetchStatus).toBe('fetching');
    expect(gates).toHaveLength(1);

    applyPatchLocally(client, request, response);
    const applied = applyPatchToSlice(before, request, response);

    // Відповідь старого читання (БД до коміту): C1=1, версія v1.
    gates[0]?.(slice(1, 'v1'));
    await settle();

    const cached = client.getQueryData<TableSliceDto>(key);
    expect(cached?.rows[0]?.rowVersion).toBe('v2');
    expect(cached?.rows[0]?.cells).toEqual(applied.rows[0]?.cells);

    // Читання перезапущено — воно бачить уже закомічене.
    expect(gates).toHaveLength(2);
    gates[1]?.(slice(7, 'v2'));
    await settle();

    expect(client.getQueryData<TableSliceDto>(key)?.rows[0]?.rowVersion).toBe('v2');
    expect(client.getQueryState(key)?.status).toBe('success');
  });

  it('без читання в дорозі — жодного запиту (CL-01 лишається)', async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    let calls = 0;
    await client.prefetchQuery({
      queryKey: key,
      queryFn: () => {
        calls += 1;
        return Promise.resolve(slice(1, 'v1'));
      },
    });
    expect(calls).toBe(1);

    applyPatchLocally(client, request, response);
    await settle();

    expect(calls).toBe(1);
    expect(client.getQueryData<TableSliceDto>(key)?.rows[0]?.rowVersion).toBe('v2');
  });
});

import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider, useQuery } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import type { PatchCellsRequest, PatchCellsResponse, RowDto, TableSliceDto } from '@/api/types';
import { queryKeys } from '@/api/queryKeys';
import { applyPatchToSlice } from '../sliceApply';
import { applySliceCachePolicy } from '../sliceCache';
import { buildRequest, useCellPatch } from '../useCellPatch';

/**
 * `CL-01` (`DIRECTIVE-14-ARCH.md` §3.5): успішний `PATCH` не тягне за собою
 * `GET` зрізу.
 *
 * ⛔ Доказ — ЛІЧИЛЬНИК ЗАПИТІВ, а не час. Предмет тут не швидкість, а
 * кількість звернень: `GET /documents/{id}/tables/{id}` — найважчий
 * регулярний запит системи, і питання рівно одне — скільки разів його кличуть
 * на одне збереження. Замір часу відповів би на інше питання й залежав би від
 * машини.
 *
 * ⚠ У зрізу МУСИТЬ бути активний спостерігач (`useQuery` нижче), інакше тест
 * порожній: `invalidateQueries` за замовчуванням перезапитує лише активні
 * запити, і без змонтованої сітки навіть старий код не зробив би жодного
 * `GET`. Саме тому хук тут рендериться разом із запитом зрізу.
 */

const DocumentId = 7;
const TableInstanceId = 700;
const PeriodKey = 202609;

const SliceUrl = `/api/v1/documents/${String(DocumentId)}/tables/${String(TableInstanceId)}`;
const PatchUrl = `/api/v1/documents/${String(DocumentId)}/cells`;

function sliceFixture(): TableSliceDto {
  return {
    tableInstanceId: TableInstanceId,
    periodKey: PeriodKey,
    columns: [],
    rows: [
      {
        rowKey: 'R1',
        ordinal: 1,
        rowKind: 'Static',
        label: null,
        rowVersion: '0x01',
        cells: { C1: 10, C2: 20 },
        isOrphaned: false,
      },
      {
        rowKey: 'R2',
        ordinal: 2,
        rowKind: 'Static',
        label: null,
        rowVersion: '0x0A',
        cells: { C1: 99 },
        isOrphaned: false,
      },
    ],
    cellPermissions: {},
    cellConfirmations: {},
  } as TableSliceDto;
}

const patchResponse: PatchCellsResponse = {
  appliedCells: 1,
  rowVersions: { R1: '0x02' },
  validation: [],
};

/**
 * Рядок за позицією.
 *
 * ⚠ Кидає, а не повертає `undefined`: `noUncheckedIndexedAccess` інакше
 * змусив би обвішати кожне очікування знаком питання, і тест мовчки проходив
 * би на порожньому зрізі.
 */
function rowAt(slice: TableSliceDto | undefined, index: number): RowDto {
  const row = slice?.rows[index];
  if (row === undefined) throw new Error(`у зрізі немає рядка ${String(index)}`);

  return row;
}

/** Скільки разів попросили саме зріз. */
function countSliceReads(fetchMock: ReturnType<typeof vi.fn>): number {
  return fetchMock.mock.calls.filter(([url]) => String(url) === SliceUrl).length;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('CL-01 · збереження не перезапитує зріз', () => {
  it('після успішного PATCH — нуль GET зрізу, а значення й версія оновлені локально', async () => {
    const fetchMock = vi.fn(async (url: unknown) => {
      const body = String(url) === PatchUrl ? patchResponse : sliceFixture();

      return new Response(JSON.stringify(body), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    });

    vi.stubGlobal('fetch', fetchMock);

    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    // ⚠ Та сама політика, що й у застосунку (`CL-02`): без неї `staleTime`
    // зрізу — нуль, тобто запит застарілий завжди, і перевірка «зріз
    // позначено застарілим» нижче не доводила б нічого (перевірено мутацією:
    // вона проходила й БЕЗ позначки).
    applySliceCachePolicy(client);

    function wrapper({ children }: { children: ReactNode }) {
      return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
    }

    const { result } = renderHook(
      () => ({
        slice: useQuery({
          queryKey: queryKeys.slices.one(TableInstanceId, PeriodKey),
          queryFn: () => fetch(SliceUrl).then((r) => r.json() as Promise<TableSliceDto>),
        }),
        patch: useCellPatch(DocumentId),
      }),
      { wrapper },
    );

    await waitFor(() => expect(result.current.slice.data).toBeDefined());
    expect(countSliceReads(fetchMock)).toBe(1);

    await act(async () => {
      await result.current.patch.patch(
        buildRequest(TableInstanceId, PeriodKey, [
          { rowKey: 'R1', columnCode: 'C1', value: 42, isEmpty: false, baseVersion: '0x01' },
        ]),
      );
    });

    // ⛔ Ядро перевірки: жодного нового читання зрізу.
    expect(countSliceReads(fetchMock)).toBe(1);

    // ⚠ І при цьому дані на екрані свіжі: значення — своє, версія — з
    // відповіді. Без цієї половини «нуль GET» означав би просто застарілу
    // сітку.
    const shown = client.getQueryData<TableSliceDto>(
      queryKeys.slices.one(TableInstanceId, PeriodKey),
    );

    expect(rowAt(shown, 0).cells.C1).toBe(42);
    expect(rowAt(shown, 0).rowVersion).toBe('0x02');

    // Сусідній рядок не зачеплений — ні значенням, ні версією.
    expect(rowAt(shown, 1).cells.C1).toBe(99);
    expect(rowAt(shown, 1).rowVersion).toBe('0x0A');

    // ⚠ Зріз позначено застарілим — щоб обчислені колонки прийшли при
    // наступному монтуванні сітки, — але саме позначено, без запиту: інакше
    // «нуль GET» вище було б куплено ціною застарілої таблиці назавжди.
    expect(
      client.getQueryCache().find({ queryKey: queryKeys.slices.one(TableInstanceId, PeriodKey) })
        ?.isStale(),
    ).toBe(true);

    expect(countSliceReads(fetchMock)).toBe(1);
  });
});

describe('CL-01 · застосування відповіді до зрізу', () => {
  const request: PatchCellsRequest = {
    tableInstanceId: TableInstanceId,
    periodKey: PeriodKey,
    origin: 'UserEdit',
    rows: [
      {
        rowKey: 'R1',
        baseVersion: '0x01',
        cells: [
          { columnCode: 'C1', value: 42, isEmpty: false },
          { columnCode: 'C2', value: null, isEmpty: true },
        ],
      },
    ],
  };

  it('R-B4: явна порожнеча лишає ключ зі значенням null, стирання прибирає ключ', () => {
    const next = applyPatchToSlice(sliceFixture(), request, patchResponse);

    expect('C2' in rowAt(next, 0).cells).toBe(true);
    expect(rowAt(next, 0).cells.C2).toBeNull();

    const erased = applyPatchToSlice(
      sliceFixture(),
      {
        ...request,
        rows: [
          {
            rowKey: 'R1',
            baseVersion: '0x01',
            cells: [{ columnCode: 'C2', value: null, isEmpty: false }],
          },
        ],
      },
      patchResponse,
    );

    expect('C2' in rowAt(erased, 0).cells).toBe(false);
  });

  it('повертає НОВИЙ об’єкт зрізу і новий рядок — інакше сітка не перемалюється', () => {
    const before = sliceFixture();
    const after = applyPatchToSlice(before, request, patchResponse);

    expect(after).not.toBe(before);
    expect(rowAt(after, 0)).not.toBe(rowAt(before, 0));

    // ⚠ Незачеплений рядок лишається ТИМ САМИМ об'єктом: мемоїзація за
    // рядками (`rowIndex.ts`, `CL-03`) і порівняння посилань у сітці мають
    // на це право.
    expect(rowAt(after, 1)).toBe(rowAt(before, 1));
    expect(rowAt(before, 0).cells.C1).toBe(10);
  });

  it('батч чужого екземпляра таблиці не застосовується', () => {
    const before = sliceFixture();
    const after = applyPatchToSlice(before, { ...request, tableInstanceId: 999 }, patchResponse);

    expect(after).toBe(before);
  });

  it('версія без запису у відповіді лишається старою, а не зникає', () => {
    const after = applyPatchToSlice(sliceFixture(), request, {
      appliedCells: 1,
      rowVersions: {},
      validation: [],
    });

    expect(rowAt(after, 0).rowVersion).toBe('0x01');
  });
});

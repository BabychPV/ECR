import type { ReactNode } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, renderHook, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { queryKeys } from '@/api/queryKeys';
import type { TableSliceDto } from '@/api/types';
import { EcrApiError } from '@/api/client';
import { useCellStyles } from '../useCellStyles';

const apiFetch = vi.hoisted(() => vi.fn());
const show = vi.hoisted(() => vi.fn());

vi.mock('@/api/client', async (importOriginal) => ({ ...(await importOriginal<typeof import('@/api/client')>()), apiFetch }));
vi.mock('@mantine/notifications', () => ({ notifications: { show } }));

const sliceKey = queryKeys.slices.one(5, 202609);
const slice = { tableInstanceId: 5, periodKey: 202609, columns: [], rows: [], cellPermissions: {}, cellConfirmations: {}, cellStyles: null } as unknown as TableSliceDto;

function setup() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  client.setQueryData(sliceKey, slice);
  const wrapper = ({ children }: { children: ReactNode }) => <QueryClientProvider client={client}>{children}</QueryClientProvider>;

  return { client, ...renderHook(() => useCellStyles(1, 5, 202609), { wrapper }) };
}

const cell = (i: number) => ({ rowKey: `R${String(i)}`, columnCode: 'A', backgroundHex: '#ffff00' });

describe('useCellStyles', () => {
  beforeEach(() => {
    apiFetch.mockReset();
    show.mockReset();
  });
  afterEach(() => vi.restoreAllMocks());

  it('шле PatchCellStylesRequest: periodKey, items, reset', async () => {
    apiFetch.mockResolvedValue({});
    const { result } = setup();

    await act(() => result.current.mutateAsync({ cells: [cell(1)] }));

    const [, init] = apiFetch.mock.calls[0] as [string, { method: string; body: string }];
    expect(init.method).toBe('PATCH');
    expect(JSON.parse(init.body)).toEqual({ periodKey: 202609, items: [cell(1)], reset: false });
  });

  it('понад 1000 комірок — порції ≤ 1000 послідовно', async () => {
    apiFetch.mockResolvedValue({});
    const { result } = setup();

    await act(() => result.current.mutateAsync({ cells: Array.from({ length: 2500 }, (_, i) => cell(i)) }));

    const sizes = apiFetch.mock.calls.map(([, init]) => (JSON.parse((init as { body: string }).body) as { items: unknown[] }).items.length);
    expect(sizes).toEqual([1000, 1000, 500]);
  });

  it('відмова: відкат, один тост з role=alert-маркером, зріз інвалідовано', async () => {
    apiFetch.mockRejectedValue(new Error('boom'));
    const { result, client } = setup();
    const invalidate = vi.spyOn(client, 'invalidateQueries');

    await act(async () => {
      await result.current.mutateAsync({ cells: [cell(1)] }).catch(() => undefined);
    });

    expect(client.getQueryData<TableSliceDto>(sliceKey)?.cellStyles).toBeNull();
    expect(show).toHaveBeenCalledTimes(1);
    expect(show.mock.calls[0]?.[0]).toMatchObject({ 'data-testid': 'cell-color-toast', color: 'statusError' });
    await waitFor(() => expect(invalidate).toHaveBeenCalledWith({ queryKey: sliceKey }));
  });

  it('409 ECR-CELL-0409: тост «повторіть» і інвалідація зрізу', async () => {
    apiFetch.mockRejectedValue(new EcrApiError({ status: 409, errorCode: 'ECR-CELL-0409', title: 'Conflict' } as never));
    const { result, client } = setup();
    const invalidate = vi.spyOn(client, 'invalidateQueries');

    await act(async () => {
      await result.current.mutateAsync({ cells: [cell(1)] }).catch(() => undefined);
    });

    expect(show.mock.calls[0]?.[0]).toMatchObject({ color: 'statusWarning', 'data-testid': 'cell-color-toast' });
    await waitFor(() => expect(invalidate).toHaveBeenCalledWith({ queryKey: sliceKey }));
  });

  it('некоректний колір: нічого не йде на сервер й кеш не міняється', async () => {
    const { result, client } = setup();

    await act(async () => {
      await result.current.mutateAsync({ cells: [{ rowKey: 'R1', columnCode: 'A', backgroundHex: 'red' }] }).catch(() => undefined);
    });

    expect(apiFetch).not.toHaveBeenCalled();
    expect(client.getQueryData<TableSliceDto>(sliceKey)?.cellStyles).toBeNull();
  });

  it('область: ОДИН PATCH зі scope замість тисяч items', async () => {
    apiFetch.mockResolvedValue({ appliedCells: 5000, resetCells: 0, deniedCells: 0, skippedCells: 0 });
    const { result } = setup();

    await act(() =>
      result.current.mutateAsync({
        cells: [],
        areas: [{ allRows: true, columnCodes: ['A'] }],
        style: { backgroundHex: '#ffff00' },
      }),
    );

    expect(apiFetch).toHaveBeenCalledTimes(1);
    const [, init] = apiFetch.mock.calls[0] as [string, { body: string }];
    expect(JSON.parse(init.body)).toEqual({
      periodKey: 202609,
      reset: false,
      scope: { allRows: true, allColumns: false, columnCodes: ['A'], reset: false, backgroundHex: '#ffff00', foregroundHex: null, isBold: null },
    });
  });

  it('часткова відмова області: тост «N комірок не змінено», зріз інвалідовано', async () => {
    apiFetch.mockResolvedValue({ appliedCells: 3, resetCells: 0, deniedCells: 2, deniedByReason: { NoWriteRight: 2 }, skippedCells: 0 });
    const { result, client } = setup();
    const invalidate = vi.spyOn(client, 'invalidateQueries');

    await act(() => result.current.mutateAsync({ cells: [cell(1)], areas: [{ allRows: true, allColumns: true }] }));

    expect(show.mock.calls[0]?.[0]).toMatchObject({ color: 'statusWarning', 'data-testid': 'cell-color-toast' });
    await waitFor(() => expect(invalidate).toHaveBeenCalledWith({ queryKey: sliceKey }));
  });

  describe('perf: без повного перечитування зрізу після підтвердженого запису', () => {
    const rowSlice = {
      ...slice,
      cellStyles: {},
      cellPermissions: { 'R1:A': 'None', 'R1:B': 'None' },
    } as unknown as TableSliceDto;
    const cells = [
      { rowKey: 'R1', columnCode: 'A', backgroundHex: '#ffff00' },
      { rowKey: 'R1', columnCode: 'B', backgroundHex: '#ffff00' },
    ];

    function setupWith(data: TableSliceDto) {
      const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
      client.setQueryData(sliceKey, data);
      const wrapper = ({ children }: { children: ReactNode }) => <QueryClientProvider client={client}>{children}</QueryClientProvider>;

      return { client, ...renderHook(() => useCellStyles(1, 5, 202609), { wrapper }) };
    }

    it('items: успіх лишає оптимістичний кеш і НЕ інвалідує зріз', async () => {
      apiFetch.mockResolvedValue({ appliedCells: 2, resetCells: 0, deniedCells: 0, skippedCells: 0 });
      const { result, client } = setupWith(rowSlice);
      const invalidate = vi.spyOn(client, 'invalidateQueries');

      await act(() => result.current.mutateAsync({ cells }));

      expect(Object.keys(client.getQueryData<TableSliceDto>(sliceKey)?.cellStyles ?? {})).toEqual(['R1:A', 'R1:B']);
      expect(invalidate).not.toHaveBeenCalled();
    });

    it('область з оптимістикою: лічильники збігаються → без інвалідації; cellPermissions не перестворюється', async () => {
      apiFetch.mockResolvedValue({ appliedCells: 1, resetCells: 0, deniedCells: 0, skippedCells: 1 });
      const { result, client } = setupWith(rowSlice);
      const invalidate = vi.spyOn(client, 'invalidateQueries');

      await act(() => result.current.mutateAsync({ cells, areas: [{ rowKeys: ['R1'], allColumns: true }], style: { backgroundHex: '#ffff00' } }));

      const data = client.getQueryData<TableSliceDto>(sliceKey);
      expect(invalidate).not.toHaveBeenCalled();
      expect(data?.cellPermissions).toBe(rowSlice.cellPermissions);
      expect(Object.keys(data?.cellStyles ?? {})).toHaveLength(2);
    });

    it('часткова відмова з відомими комірками (cellPermissions == причина): відкат лише їх, без інвалідації, тост лишається', async () => {
      const locked = {
        ...rowSlice,
        cellStyles: { 'R1:B': { backgroundHex: '#00b0f0', foregroundHex: null, isBold: false } },
        cellPermissions: { 'R1:A': 'None', 'R1:B': 'LockedByOwner' },
      } as unknown as TableSliceDto;
      apiFetch.mockResolvedValue({ appliedCells: 1, resetCells: 0, deniedCells: 1, deniedByReason: { LockedByOwner: 1 }, skippedCells: 0 });
      const { result, client } = setupWith(locked);
      const invalidate = vi.spyOn(client, 'invalidateQueries');

      await act(() => result.current.mutateAsync({ cells, areas: [{ rowKeys: ['R1'], allColumns: true }], style: { backgroundHex: '#ffff00' } }));

      const styles = client.getQueryData<TableSliceDto>(sliceKey)?.cellStyles;
      expect(styles?.['R1:A']?.backgroundHex).toBe('#ffff00');
      expect(styles?.['R1:B']?.backgroundHex).toBe('#00b0f0'); // відмовлена комірка повернута до попереднього
      expect(invalidate).not.toHaveBeenCalled();
      expect(show.mock.calls[0]?.[0]).toMatchObject({ color: 'statusWarning', 'data-testid': 'cell-color-toast' });
    });

    it('відмовлені комірки поза оптимістикою (клієнт їх не малював) → кеш підтверджено без перечитування', async () => {
      const locked = { ...rowSlice, cellPermissions: { 'R1:A': 'None', 'R1:B': 'LockedByOwner' } } as unknown as TableSliceDto;
      apiFetch.mockResolvedValue({ appliedCells: 1, resetCells: 0, deniedCells: 1, deniedByReason: { LockedByOwner: 1 }, skippedCells: 0 });
      const { result, client } = setupWith(locked);
      const invalidate = vi.spyOn(client, 'invalidateQueries');

      await act(() => result.current.mutateAsync({ cells: [cells[0] as (typeof cells)[number]], areas: [{ rowKeys: ['R1'], allColumns: true }] }));

      expect(Object.keys(client.getQueryData<TableSliceDto>(sliceKey)?.cellStyles ?? {})).toEqual(['R1:A']);
      expect(invalidate).not.toHaveBeenCalled();
    });

    it('часткова відмова, комірки якої не встановлюються однозначно → перечитування', async () => {
      apiFetch.mockResolvedValue({ appliedCells: 1, resetCells: 0, deniedCells: 1, deniedByReason: { LockedByOwner: 1 }, skippedCells: 0 });
      const { result, client } = setupWith(rowSlice); // у cellPermissions немає LockedByOwner
      const invalidate = vi.spyOn(client, 'invalidateQueries');

      await act(() => result.current.mutateAsync({ cells, areas: [{ rowKeys: ['R1'], allColumns: true }] }));

      await waitFor(() => expect(invalidate).toHaveBeenCalledWith({ queryKey: sliceKey }));
    });

    it('область: розбіжність лічильників (сервер розгорнув більше) → перечитування', async () => {
      apiFetch.mockResolvedValue({ appliedCells: 5, resetCells: 0, deniedCells: 0, skippedCells: 0 });
      const { result, client } = setupWith(rowSlice);
      const invalidate = vi.spyOn(client, 'invalidateQueries');

      await act(() => result.current.mutateAsync({ cells, areas: [{ rowKeys: ['R1'], allColumns: true }] }));

      await waitFor(() => expect(invalidate).toHaveBeenCalledWith({ queryKey: sliceKey }));
    });

    it('область без оптимістики (cells порожні) → перечитування', async () => {
      apiFetch.mockResolvedValue({ appliedCells: 9, resetCells: 0, deniedCells: 0, skippedCells: 0 });
      const { result, client } = setupWith(rowSlice);
      const invalidate = vi.spyOn(client, 'invalidateQueries');

      await act(() => result.current.mutateAsync({ cells: [], areas: [{ allRows: true, allColumns: true }] }));

      await waitFor(() => expect(invalidate).toHaveBeenCalledWith({ queryKey: sliceKey }));
    });

    it('підтверджений запис, поки зріз саме читається, → перезапит у дорозі (G1-01)', async () => {
      apiFetch.mockResolvedValue({ appliedCells: 2, resetCells: 0, deniedCells: 0, skippedCells: 0 });
      const { result, client } = setupWith(rowSlice);
      vi.spyOn(client, 'getQueryState').mockReturnValue({ fetchStatus: 'fetching' } as never);
      const invalidate = vi.spyOn(client, 'invalidateQueries');

      await act(() => result.current.mutateAsync({ cells }));

      expect(invalidate).toHaveBeenCalledWith({ queryKey: sliceKey, refetchType: 'all' });
    });
  });

  it('понад 50 000 комірок: кілька запитів по rowKeys, відмови підсумовуються', async () => {
    apiFetch.mockResolvedValue({ appliedCells: 10, resetCells: 0, deniedCells: 1, deniedByReason: { NoWriteRight: 1 }, skippedCells: 0 });
    const { result } = setup();

    await act(() => result.current.mutateAsync({ cells: [cell(1)], areas: [{ rowKeys: ['R1'], allColumns: true }, { rowKeys: ['R2'], allColumns: true }] }));

    expect(apiFetch).toHaveBeenCalledTimes(2);
    expect(show.mock.calls[0]?.[0]).toMatchObject({ color: 'statusWarning' });
  });
});

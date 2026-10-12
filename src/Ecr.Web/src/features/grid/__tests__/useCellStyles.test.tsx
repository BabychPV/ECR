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
        cells: Array.from({ length: 5000 }, (_, i) => cell(i)),
        areas: [{ allRows: true, columnCodes: ['A'] }],
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

  it('понад 50 000 комірок: кілька запитів по rowKeys, відмови підсумовуються', async () => {
    apiFetch.mockResolvedValue({ appliedCells: 10, resetCells: 0, deniedCells: 1, deniedByReason: { NoWriteRight: 1 }, skippedCells: 0 });
    const { result } = setup();

    await act(() => result.current.mutateAsync({ cells: [cell(1)], areas: [{ rowKeys: ['R1'], allColumns: true }, { rowKeys: ['R2'], allColumns: true }] }));

    expect(apiFetch).toHaveBeenCalledTimes(2);
    expect(show.mock.calls[0]?.[0]).toMatchObject({ color: 'statusWarning' });
  });
});

import type { ReactNode } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import * as rowsApi from '@/features/registries/rows/api';
import { createLimiter, PickerConcurrency } from '../readLimiter';
import { fieldsOf } from '../model';
import { usePickerData } from '../usePermitData';
import { column, page, permit, registryRow } from './permitFixtures';

/**
 * Пул читань пікерів (P3 «Аналіза»): не більше {@link PickerConcurrency} запитів одночасно.
 *
 * Мутаційні докази (виконано вручну, мутацію відновлено):
 * - `createLimiter`: `active < max` → `active < max + 100` валить обидва тести ліміту;
 * - `usePermitData`: `readAll(…, pickerReads)` → `readAll(…)` (без пулу) валить «десять реєстрів…» піком 10.
 */
vi.mock('@/features/registries/rows/api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/features/registries/rows/api')>()),
  getRegistryRows: vi.fn(),
}));

const getRows = vi.mocked(rowsApi.getRegistryRows);
afterEach(() => vi.clearAllMocks());

describe('createLimiter', () => {
  it('понад max завдань чекають у черзі; пік у польоті не перевищує max, порядок збережено', async () => {
    const run = createLimiter(3);
    let inFlight = 0;
    let peak = 0;
    const order: number[] = [];
    const gates: (() => void)[] = [];

    const jobs = Array.from({ length: 8 }, (_, index) =>
      run(async () => {
        inFlight += 1;
        peak = Math.max(peak, inFlight);
        order.push(index);
        await new Promise<void>((resolve) => gates.push(resolve));
        inFlight -= 1;
        return index;
      }),
    );

    await waitFor(() => expect(gates).toHaveLength(3));
    expect(peak).toBe(3);
    for (let released = 0; released < 8; released += 1) {
      await waitFor(() => expect(gates.length).toBeGreaterThan(released));
      gates[released]?.();
    }
    expect(await Promise.all(jobs)).toEqual([0, 1, 2, 3, 4, 5, 6, 7]);
    expect(peak).toBe(3);
    expect(order).toEqual([0, 1, 2, 3, 4, 5, 6, 7]);
  });

  it('відмова завдання звільняє місце: наступні виконуються', async () => {
    const run = createLimiter(1);
    const failing = run(() => Promise.reject(new Error('boom')));
    const next = run(() => Promise.resolve('ok'));
    await expect(failing).rejects.toThrow('boom');
    await expect(next).resolves.toBe('ok');
  });
});

describe('usePickerData', () => {
  it('десять реєстрів-довідників: у польоті не більше PickerConcurrency запитів, усі десять прочитано', async () => {
    const codes = Array.from({ length: 10 }, (_, index) => `REG${String(index)}`);
    const table = {
      code: 'T',
      nameL10n: { en: 'T' },
      order: 1,
      rules: [],
      columns: codes.map((code, index) =>
        column(`C${String(index)}`, { dataType: 'Lookup', listScope: 'SharedRegistry', listRegistryCode: code, order: index + 1 }),
      ),
    };
    const fields = fieldsOf(table as never, new Map());

    let inFlight = 0;
    let peak = 0;
    getRows.mockImplementation(async (code) => {
      inFlight += 1;
      peak = Math.max(peak, inFlight);
      await new Promise((resolve) => setTimeout(resolve, 5));
      inFlight -= 1;
      return page([registryRow(1, {}, { display: `label-${code}` })]);
    });

    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const wrapper = ({ children }: { children: ReactNode }) => <QueryClientProvider client={client}>{children}</QueryClientProvider>;
    const { result } = renderHook(() => usePickerData(permit(), fields, true), { wrapper });

    await waitFor(() => expect(result.current.pending).toBe(false), { timeout: 5000 });
    expect(getRows).toHaveBeenCalledTimes(10);
    expect(peak).toBeGreaterThan(1);
    expect(peak).toBeLessThanOrEqual(PickerConcurrency);
    expect(Object.keys(result.current.data.sources.shared ?? {}).sort()).toEqual([...codes].sort());
  });
});

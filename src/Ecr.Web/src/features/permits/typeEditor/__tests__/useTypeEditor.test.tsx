import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { EcrApiError } from '@/api/client';
import { hasUnsavedChanges, flushUnsaved } from '@/shared/ui/unsavedSources';
import * as typesApi from '../../api/typesApi';
import { addColumn, removeColumn, updateColumn } from '../draftModel';
import { AutosaveDelayMs, useTypeEditor } from '../useTypeEditor';
import { column, permitType } from './fixtures';

vi.mock('@/features/permits/api/typesApi', () => ({
  getPermitType: vi.fn(),
  replacePermitType: vi.fn(),
}));

/**
 * Робоча копія і збереження (PM-08b).
 *
 * Мутаційний доказ (виконано вручну, відновлено): прибране `if (conflicted.current || …)` у `runSave`
 * валить «конфлікт»; без перевірки `problemsOf(…).length > 0` падає «неповна копія не відправляється»;
 * `clearTimer()` у `schedule` прибраний — «дві правки = один запит» падає.
 */

const get = vi.mocked(typesApi.getPermitType);
const put = vi.mocked(typesApi.replacePermitType);

function wrapper(): ({ children }: { children: ReactNode }) => ReactNode {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return function Wrapper({ children }) {
    return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
  };
}

function problem(errorCode: string): EcrApiError {
  return new EcrApiError({ title: 'x', status: 409, errorCode, correlationId: 'c-1', extensions2: { messageKey: 'err.x' } });
}

/** Відповідь сервера: те, що надіслали, з новим токеном. */
function echo(rowVersion: string) {
  return (_code: string, body: Parameters<typeof typesApi.replacePermitType>[1]) =>
    Promise.resolve(permitType({ ...body, rowVersion }));
}

async function open(type = permitType()) {
  get.mockResolvedValue(type);
  const view = renderHook(() => useTypeEditor('WaterDischarge'), { wrapper: wrapper() });
  await waitFor(() => expect(view.result.current.api).not.toBeNull());
  return view;
}

beforeEach(() => {
  vi.clearAllMocks();
});

afterEach(() => {
  vi.useRealTimers();
  cleanup();
});

/** Годинник підміняємо лише ПІСЛЯ завантаження: react-query сам користується таймерами. */
async function pass(ms: number): Promise<void> {
  await act(async () => {
    await vi.advanceTimersByTimeAsync(ms);
  });
}

describe('завантаження', () => {
  it('робоча копія = відповідь сервера, чернетка правна', async () => {
    const { result } = await open();
    expect(result.current.api!.draft.tables.map((t) => t.code)).toEqual(['Discharge', 'Storage']);
    expect(result.current.api!.editable).toBe(true);
    expect(result.current.api!.save).toEqual({ kind: 'idle' });
    expect(result.current.problems).toEqual([]);
  });

  it('опублікований тип без чернетки не правиться', async () => {
    const { result } = await open(permitType({ status: 'Published', hasUnpublishedChanges: false, draftVersion: null, version: 2 }));
    expect(result.current.api!.editable).toBe(false);
    act(() => result.current.api!.apply((d) => removeColumn(d, 'Discharge', 'Annual')));
    expect(result.current.api!.save).toEqual({ kind: 'idle' });
  });

  it('помилка читання лишається в query, api немає', async () => {
    get.mockRejectedValue(problem('ECR-PRM-0404'));
    const { result } = renderHook(() => useTypeEditor('Nope'), { wrapper: wrapper() });
    await waitFor(() => expect(result.current.query.isError).toBe(true));
    expect(result.current.api).toBeNull();
  });
});

describe('збереження', () => {
  it('правка → «N unsaved changes» → PUT з rowVersion → Saved; наступний PUT з новим токеном', async () => {
    const { result } = await open();
    vi.useFakeTimers();
    put.mockImplementationOnce(echo('rv-2')).mockImplementationOnce(echo('rv-3'));

    act(() => result.current.api!.apply((d) => addColumn(d, 'Storage', column('Note'))));
    expect(result.current.api!.save).toEqual({ kind: 'dirty', count: 1 });
    expect(put).not.toHaveBeenCalled();

    await pass(AutosaveDelayMs);
    expect(put).toHaveBeenCalledTimes(1);
    expect(put.mock.calls[0]![0]).toBe('WaterDischarge');
    expect(put.mock.calls[0]![1]).toMatchObject({ rowVersion: 'rv-1', category: 'Water' });
    expect(put.mock.calls[0]![1].tables[1]!.columns.map((c) => c.code)).toEqual(['Place', 'Note']);
    expect(result.current.api!.save.kind).toBe('saved');

    act(() => result.current.api!.apply((d) => updateColumn(d, 'Storage', 'Note', { group: 'G' })));
    await pass(AutosaveDelayMs);
    expect(put.mock.calls[1]![1]).toMatchObject({ rowVersion: 'rv-2' });
  });

  it('дві правки поспіль = один запит', async () => {
    const { result } = await open();
    vi.useFakeTimers();
    put.mockImplementation(echo('rv-2'));
    act(() => result.current.api!.apply((d) => addColumn(d, 'Storage', column('A1'))));
    await pass(AutosaveDelayMs - 100);
    act(() => result.current.api!.apply((d) => addColumn(d, 'Storage', column('A2'))));
    await pass(AutosaveDelayMs - 100);
    expect(put).not.toHaveBeenCalled();
    await pass(200);
    expect(put).toHaveBeenCalledTimes(1);
    expect(put.mock.calls[0]![1].tables[1]!.columns).toHaveLength(3);
  });

  it('неповна копія не відправляється: проблеми видно, правка не губиться', async () => {
    const { result } = await open();
    vi.useFakeTimers();
    act(() => result.current.api!.apply((d) => updateColumn(d, 'Storage', 'Place', { key: false })));
    await pass(AutosaveDelayMs * 2);
    expect(put).not.toHaveBeenCalled();
    expect(result.current.problems.map((p) => p.messageKey)).toEqual(['err.ECR-PRM-0422.tableWithoutKey']);
    expect(result.current.api!.save).toEqual({ kind: 'dirty', count: 1 });
    // Виправили — пішло.
    put.mockResolvedValue(permitType({ rowVersion: 'rv-2' }));
    act(() => result.current.api!.apply((d) => updateColumn(d, 'Storage', 'Place', { key: true })));
    await pass(AutosaveDelayMs);
    expect(put).not.toHaveBeenCalled(); // копія знову дорівнює збереженій — зберігати нічого
    expect(result.current.api!.save.kind).toBe('idle');
  });

  it('правка під час запиту не губиться', async () => {
    const { result } = await open();
    vi.useFakeTimers();
    let release: (value: ReturnType<typeof permitType>) => void = () => undefined;
    put
      .mockImplementationOnce(() => new Promise((resolve) => (release = resolve)))
      .mockResolvedValueOnce(permitType({ rowVersion: 'rv-3' }));
    act(() => result.current.api!.apply((d) => addColumn(d, 'Storage', column('First'))));
    await pass(AutosaveDelayMs);
    expect(result.current.api!.save.kind).toBe('saving');
    act(() => result.current.api!.apply((d) => addColumn(d, 'Storage', column('Second'))));
    await act(async () => release(permitType({ rowVersion: 'rv-2' })));
    expect(result.current.api!.draft.tables[1]!.columns.map((c) => c.code)).toEqual(['Place', 'First', 'Second']);
    await pass(AutosaveDelayMs);
    expect(put).toHaveBeenCalledTimes(2);
    expect(put.mock.calls[1]![1]).toMatchObject({ rowVersion: 'rv-2' });
  });
});

describe('відмови', () => {
  it('конфлікт 4093: стан conflict, копія лишається, повторів немає; reload відкидає її', async () => {
    const { result } = await open();
    vi.useFakeTimers();
    put.mockRejectedValue(problem('ECR-PRM-4093'));
    act(() => result.current.api!.apply((d) => addColumn(d, 'Storage', column('Mine'))));
    await pass(AutosaveDelayMs);
    expect(result.current.api!.save).toEqual({ kind: 'conflict' });
    expect(result.current.saveError).toBeInstanceOf(EcrApiError);
    act(() => result.current.api!.apply((d) => addColumn(d, 'Storage', column('More'))));
    await pass(AutosaveDelayMs * 3);
    expect(put).toHaveBeenCalledTimes(1);
    expect(result.current.api!.draft.tables[1]!.columns).toHaveLength(3);
    // Навіть явне збереження (вихід зі сторінки) не б'є знову по застарілому токену.
    let flushed = true;
    await act(async () => {
      flushed = await result.current.api!.flush();
    });
    expect(flushed).toBe(false);
    expect(put).toHaveBeenCalledTimes(1);

    get.mockResolvedValue(permitType({ rowVersion: 'rv-9' }));
    await act(async () => {
      await result.current.api!.reload();
    });
    expect(result.current.api!.draft.tables[1]!.columns).toHaveLength(1);
    expect(result.current.api!.save).toEqual({ kind: 'idle' });
    expect(result.current.saveError).toBeNull();
  });

  it('інша помилка: failed з лічильником, retry повторює запит', async () => {
    const { result } = await open();
    vi.useFakeTimers();
    put.mockRejectedValueOnce(problem('ECR-SYS-0500')).mockResolvedValueOnce(permitType({ rowVersion: 'rv-2' }));
    act(() => result.current.api!.apply((d) => addColumn(d, 'Storage', column('Mine'))));
    await pass(AutosaveDelayMs);
    expect(result.current.api!.save).toEqual({ kind: 'failed', count: 1 });
    await act(async () => result.current.retry());
    expect(put).toHaveBeenCalledTimes(2);
    expect(result.current.api!.save.kind).toBe('saved');
    expect(result.current.saveError).toBeNull();
  });
});

describe('вихід зі сторінки', () => {
  it('джерело незбереженого: є правка — hasUnsaved, flush зберігає', async () => {
    const { result } = await open();
    vi.useFakeTimers();
    expect(hasUnsavedChanges()).toBe(false);
    put.mockResolvedValue(permitType({ rowVersion: 'rv-2' }));
    act(() => result.current.api!.apply((d) => addColumn(d, 'Storage', column('Mine'))));
    expect(hasUnsavedChanges()).toBe(true);
    let ok = false;
    await act(async () => {
      ok = await flushUnsaved(1000);
    });
    expect(ok).toBe(true);
    expect(put).toHaveBeenCalledTimes(1);
    expect(hasUnsavedChanges()).toBe(false);
  });

  it('flush неповної копії — false: людина лишається з даними', async () => {
    const { result } = await open();
    act(() => result.current.api!.apply((d) => updateColumn(d, 'Storage', 'Place', { key: false })));
    let ok = true;
    await act(async () => {
      ok = await result.current.api!.flush();
    });
    expect(ok).toBe(false);
    expect(put).not.toHaveBeenCalled();
  });
});

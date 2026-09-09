import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { buildRequest, useCellPatch } from '../useCellPatch';
import type { PatchCellsResponse } from '@/api/types';

/**
 * Видимий стан збереження (`B-35`, `#38`).
 *
 * ⛔ D-134: мовчазне автозбереження — небезпека, а не зручність. Ці тести
 * доводять, що `status` справді проходить `saving → saved`/`error`, а не
 * лише що `patch()` не падає.
 */

function respond(body: unknown, status = 200): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () =>
      Promise.resolve(
        new Response(JSON.stringify(body), {
          status,
          headers: { 'Content-Type': 'application/json' },
        }),
      ),
    ),
  );
}

function wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('useCellPatch — видимий індикатор', () => {
  it('idle → saving → saved на успішному збереженні', async () => {
    const response: PatchCellsResponse = { appliedCells: 1, rowVersions: { R1: '0x02' }, validation: [] };
    respond(response);

    const { result } = renderHook(() => useCellPatch(7), { wrapper });

    expect(result.current.status).toBe('idle');

    let pending: Promise<PatchCellsResponse>;
    act(() => {
      pending = result.current.patch(buildRequest(700, 202609, []));
    });

    // ⚠ Синхронно після виклику: оператор має бачити «зберігається», а не
    // дізнатися про це вже по факту завершення запиту.
    expect(result.current.status).toBe('saving');

    await act(async () => {
      await pending;
    });

    expect(result.current.status).toBe('saved');
  });

  it('status повертається в idle після завершення видимого вікна', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });

    const response: PatchCellsResponse = { appliedCells: 0, rowVersions: {}, validation: [] };
    respond(response);

    const { result } = renderHook(() => useCellPatch(7), { wrapper });

    await act(async () => {
      await result.current.patch(buildRequest(700, 202609, []));
    });

    expect(result.current.status).toBe('saved');

    await act(async () => {
      vi.advanceTimersByTime(2100);
    });

    await waitFor(() => expect(result.current.status).toBe('idle'));

    vi.useRealTimers();
  });

  it('status стає error, якщо сервер відхиляє запит', async () => {
    respond({ title: 'Конфлікт', status: 409, errorCode: 'ECR-CELL-0409', correlationId: 'c1' }, 409);

    const { result } = renderHook(() => useCellPatch(7), { wrapper });

    await act(async () => {
      await expect(result.current.patch(buildRequest(700, 202609, []))).rejects.toThrow();
    });

    expect(result.current.status).toBe('error');
  });
});

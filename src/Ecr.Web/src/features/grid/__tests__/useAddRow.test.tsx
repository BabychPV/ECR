import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { EcrApiError, SheetBeingSubmittedMessageKey } from '@/api/client';
import { addRowRetryDelayMs, shouldRetryAddRow, useAddRow } from '../useAddRow';

/**
 * Y7-03 (клієнт): додавання рядка повторюється саме після минущої `409 sheetBeingSubmitted`, але не після
 * стелі рядків (`ECR-ROW-0409`).
 *
 * ⛔ Мутаційні докази: прибери `retry` з `useAddRow` — падає «повторюється після sheetBeingSubmitted»;
 * зроби `shouldRetryAddRow` безумовним — падає «ECR-ROW-0409 не повторюється».
 */
const showApiError = vi.hoisted(() => vi.fn());
vi.mock('@/shared/ui/notify', () => ({ showApiError, showWarning: vi.fn() }));

function json(body: unknown, status: number): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

const busy = () =>
  json(
    { title: 'Busy', status: 409, errorCode: 'ECR-DOC-4091', messageKey: SheetBeingSubmittedMessageKey },
    409,
  );

let client = new QueryClient({ defaultOptions: { mutations: { retry: false } } });

const wrapper = ({ children }: { children: ReactNode }) => (
  <QueryClientProvider client={client}>{children}</QueryClientProvider>
);

describe('useAddRow', () => {
  const fetchMock = vi.fn<(input: RequestInfo | URL) => Promise<Response>>();

  beforeEach(() => {
    client = new QueryClient({ defaultOptions: { mutations: { retry: false } } });
    fetchMock.mockReset();
    showApiError.mockReset();
    vi.stubGlobal('fetch', fetchMock);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    client.clear();
  });

  it('повторюється після sheetBeingSubmitted і без червоного тосту', async () => {
    fetchMock.mockResolvedValueOnce(busy()).mockResolvedValue(json({ rowKey: 'r1' }, 200));
    const onAdded = vi.fn();
    const { result } = renderHook(() => useAddRow(5, 9, onAdded), { wrapper });

    act(() => result.current.mutate());

    await waitFor(() => expect(onAdded).toHaveBeenCalledTimes(1), { timeout: 4000 });
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(showApiError).not.toHaveBeenCalled();
  }, 8000);

  it('ECR-ROW-0409 не повторюється й показується', async () => {
    fetchMock.mockResolvedValue(
      json({ title: 'Limit', status: 409, errorCode: 'ECR-ROW-0409', messageKey: 'err.ECR-ROW-0409.limit' }, 409),
    );
    const { result } = renderHook(() => useAddRow(5, 9, vi.fn()), { wrapper });

    act(() => result.current.mutate());

    await waitFor(() => expect(showApiError).toHaveBeenCalledTimes(1));
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('політика: ліміт спроб і Retry-After як нижня межа відступу', () => {
    const transient = new EcrApiError({
      title: 'Busy',
      status: 409,
      errorCode: 'ECR-DOC-4091',
      correlationId: 'c',
      extensions2: { messageKey: SheetBeingSubmittedMessageKey },
      retryAfterSeconds: 7,
    });

    expect(shouldRetryAddRow(0, transient)).toBe(true);
    expect(shouldRetryAddRow(2, transient)).toBe(false);
    expect(addRowRetryDelayMs(0, transient)).toBe(7000);
    expect(addRowRetryDelayMs(1, new Error('x'))).toBe(2000);
  });
});

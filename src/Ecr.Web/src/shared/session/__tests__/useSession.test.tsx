import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { resetSignOutForTests } from '@/api/client';
import { useSession } from '../useSession';

/**
 * AN-97 / L9-05 / P2-06: `/me` повторюється один раз на минущий збій і не перезапитується на кожне
 * монтування споживача.
 *
 * ⛔ Мутаційні докази: поверни `retry: false` — падає «минущий збій»; поверни `staleTime: 0` — падає
 * «повторне монтування»; прибери перевірку 4xx — падає «403 не повторюється».
 */
const ME = {
  userId: 7,
  userName: 'op',
  language: 'en',
  permissions: [],
  grants: {},
  denies: [],
  mustChangePassword: false,
  isSimulation: false,
  simulatedForUserId: null,
  simulatedForUserName: null,
  simulationSessionId: null,
};

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

let client: QueryClient;
const wrapper = ({ children }: { children: ReactNode }) => (
  <QueryClientProvider client={client}>{children}</QueryClientProvider>
);

describe('useSession', () => {
  const fetchMock = vi.fn<(input: RequestInfo | URL) => Promise<Response>>();

  beforeEach(() => {
    // Глобальний дефолт тестів — без повторів: хук має оголосити свою політику сам.
    client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    fetchMock.mockReset();
    vi.stubGlobal('fetch', fetchMock);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    resetSignOutForTests();
  });

  it('минущий збій /me (503) повторюється один раз і профіль зʼявляється', async () => {
    fetchMock
      .mockResolvedValueOnce(json({ title: 'Unavailable', status: 503 }, 503))
      .mockResolvedValue(json(ME));

    const { result } = renderHook(() => useSession(), { wrapper });

    await waitFor(() => expect(result.current.data?.userId).toBe(7), { timeout: 4000 });
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(result.current.isLoadingError).toBe(false);
  }, 8000);

  it('403 не повторюється', async () => {
    fetchMock.mockResolvedValue(json({ title: 'Forbidden', status: 403, errorCode: 'ECR-AUTH-0403' }, 403));

    const { result } = renderHook(() => useSession(), { wrapper });

    await waitFor(() => expect(result.current.isLoadingError).toBe(true));
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('повторне монтування споживача не робить новий GET /me, поки профіль свіжий', async () => {
    fetchMock.mockResolvedValue(json(ME));

    const first = renderHook(() => useSession(), { wrapper });
    await waitFor(() => expect(first.result.current.data?.userId).toBe(7));
    first.unmount();

    const second = renderHook(() => useSession(), { wrapper });
    expect(second.result.current.data?.userId).toBe(7);
    await Promise.resolve();

    expect(fetchMock).toHaveBeenCalledTimes(1);
  });
});

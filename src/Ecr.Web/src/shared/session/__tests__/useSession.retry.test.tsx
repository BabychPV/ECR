import { renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { apiFetch } from '@/api/client';
import type { CurrentUserDto } from '@/api/types';
import { useSession } from '../useSession';

vi.mock('@/api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/api/client')>()),
  apiFetch: vi.fn(),
}));

/**
 * L9-05 / AN-97: транзиєнтний збій `/me` не віддає помилку одразу — запит повторюється.
 *
 * ⛔ Мутаційний доказ: поверни `retry: false` у `useSession` — тест падає.
 */
const me = { userId: 7, permissions: [] } as unknown as CurrentUserDto;

function wrapper(client: QueryClient) {
  return ({ children }: { children: ReactNode }) => <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

describe('useSession: повтор /me', () => {
  beforeEach(() => vi.mocked(apiFetch).mockReset());

  it('після одного збою /me повторюється: помилки немає, далі приходить профіль', async () => {
    vi.mocked(apiFetch).mockRejectedValueOnce(new Error('503')).mockResolvedValueOnce(me);
    const client = new QueryClient();
    const { result } = renderHook(() => useSession(), { wrapper: wrapper(client) });

    // Між збоєм і повтором запит ще «очікує»: з retry: false тут був би isError без профілю.
    await waitFor(() => expect(result.current.failureCount).toBe(1), { timeout: 5000 });
    expect(result.current.isError).toBe(false);
    expect(result.current.data).toBeUndefined();

    await waitFor(() => expect(result.current.data?.userId).toBe(7), { timeout: 5000 });
    expect(apiFetch).toHaveBeenCalledTimes(2);
    expect(result.current.isError).toBe(false);
  });
});

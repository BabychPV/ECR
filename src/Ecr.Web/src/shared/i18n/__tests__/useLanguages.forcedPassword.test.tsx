import { afterEach, describe, expect, it, vi } from 'vitest';
import type { JSX, ReactNode } from 'react';
import { renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MeQueryKey } from '@/shared/session/useSession';
import { useLanguages } from '@/shared/i18n/useLanguages';

/**
 * A3: на екрані примусової зміни пароля меню профілю тягнуло `GET /api/v1/languages` і діставало
 * `428` (приймальний прохід: «1× при відкритті меню»). Тепер запиту там немає; без профілю в кеші
 * (тести, ранній рендер) і при звичайному профілі — як і раніше.
 */
const languages = [{ code: 'en', nameNative: 'English', isDefault: true, hasTranslations: true }];

function fetchSpy() {
  const spy = vi.fn(
    async () =>
      new Response(JSON.stringify(languages), { status: 200, headers: { 'Content-Type': 'application/json' } }),
  );
  vi.stubGlobal('fetch', spy);

  return spy;
}

function wrapperWith(me: unknown): ({ children }: { children: ReactNode }) => JSX.Element {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  if (me !== undefined) client.setQueryData(MeQueryKey, me);

  return function Wrapper({ children }: { children: ReactNode }): JSX.Element {
    return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
  };
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('useLanguages: примусова зміна пароля (A3)', () => {
  it('mustChangePassword — запиту /languages немає', async () => {
    const spy = fetchSpy();

    const { result } = renderHook(() => useLanguages(), { wrapper: wrapperWith({ mustChangePassword: true }) });
    await new Promise((resolve) => setTimeout(resolve, 50));

    expect(spy).not.toHaveBeenCalled();
    expect(result.current.data).toBeUndefined();
  });

  it('звичайний профіль — запит іде і дає перелік', async () => {
    const spy = fetchSpy();

    const { result } = renderHook(() => useLanguages(), { wrapper: wrapperWith({ mustChangePassword: false }) });

    await waitFor(() => expect(result.current.data).toEqual(languages));
    expect(spy).toHaveBeenCalledTimes(1);
  });

  it('профілю в кеші ще немає — запит іде, як і раніше', async () => {
    const spy = fetchSpy();

    const { result } = renderHook(() => useLanguages(), { wrapper: wrapperWith(undefined) });

    await waitFor(() => expect(result.current.data).toEqual(languages));
    expect(spy).toHaveBeenCalledTimes(1);
  });
});

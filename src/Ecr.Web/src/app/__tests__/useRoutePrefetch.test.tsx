import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, renderHook } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { JSX, ReactNode } from 'react';
import { queryKeys } from '@/api/queryKeys';
import { routes } from '@/app/routes';
import { useRoutePrefetch } from '@/app/useRoutePrefetch';

/**
 * `useRoutePrefetch` — обробники наміру (`PR nav-arch #5`, директива C2:
 * «лише те, на що користувач реально навів курсор/фокус», не кожен рендер).
 *
 * ⛔ Дисципліна тестів тут — не «функцію можна викликати», а «прогрів
 * запускається САМЕ подією наміру (hover/focus) із затримкою, а НЕ самим
 * рендером хука, і скасовується, якщо намір не протримався достатньо
 * (mouseLeave/blur до завершення затримки)». Мутаційна перевірка (RED →
 * GREEN, вручну, перед комітом): затримку тимчасово обнулено
 * (`IntentDelayMs = 0` у `useRoutePrefetch.ts`) — ПОЧЕРВОНІЛИ два тести:
 * «прогріває дані лише ПІСЛЯ затримки наміру» (перевірка «на 149 мс іще
 * нічого» падає — при нульовій затримці прогрів уже стався) і «mouseLeave
 * ДО завершення затримки скасовує прогрів» (`setTimeout(fn, 0)` устигає
 * спрацювати ще ДО виклику `onMouseLeave` у тій самій синхронній
 * `act()`-ділянці, і прогрів відбувається, хоча тест очікує зворотного).
 * Решта п'ять лишились зеленими. Відновлено `IntentDelayMs = 150` — усі
 * 7/7 GREEN.
 */
// ⚠ Заглушки замість реальних сторінок (`routePrefetch.test.ts` пояснює
// чому): `prefetchRoute` однаково прогріває РЕАЛЬНИЙ код-чанк
// (fire-and-forget), а важке піддерево залежностей сторінки під повним
// прогоном `npm test` стабільно перевищує тестовий таймаут 5000 мс.
vi.mock('@/pages/admin/TemplatesPage', () => ({ TemplatesPage: () => null }));
vi.mock('@/pages/admin/RegistriesPage', () => ({ RegistriesPage: () => null }));
vi.mock('@/pages/admin/MethodologiesPage', () => ({ MethodologiesPage: () => null }));
vi.mock('@/pages/admin/UnitsPage', () => ({ UnitsPage: () => null }));

function jsonResponse(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

describe('useRoutePrefetch — обробники наміру (PR nav-arch #5)', () => {
  let client: QueryClient;

  beforeEach(() => {
    vi.useFakeTimers();
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => jsonResponse({ items: [], nextCursor: null, totalCount: 0 })),
    );
    client = new QueryClient();
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  function wrapper({ children }: { children: ReactNode }): JSX.Element {
    return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
  }

  it('НЕ прогріває саме на рендер хука', () => {
    const spy = vi.spyOn(client, 'prefetchQuery');

    renderHook(() => useRoutePrefetch(routes.adminTemplates.id), { wrapper });

    expect(spy).not.toHaveBeenCalled();
  });

  it('прогріває дані лише ПІСЛЯ затримки наміру від mouseEnter, і РІВНО фабричним ключем сторінки', async () => {
    const spy = vi.spyOn(client, 'prefetchQuery');
    const { result } = renderHook(() => useRoutePrefetch(routes.adminTemplates.id), { wrapper });

    act(() => {
      result.current.onMouseEnter();
    });
    expect(spy).not.toHaveBeenCalled();

    act(() => {
      vi.advanceTimersByTime(149);
    });
    expect(spy).not.toHaveBeenCalled();

    act(() => {
      vi.advanceTimersByTime(1);
    });
    expect(spy).toHaveBeenCalledTimes(1);
    expect(spy.mock.calls[0]?.[0]).toMatchObject({ queryKey: queryKeys.templates.list() });

    // Той самий виклик прогріває й чанк `TemplatesPage` (fire-and-forget) —
    // дочекатись його явно, щоб не лишити необроблений `import()` після тесту.
    await import('@/pages/admin/TemplatesPage');
  });

  it('mouseLeave ДО завершення затримки скасовує прогрів — нічого не прогріває навіть після довгого очікування', () => {
    const spy = vi.spyOn(client, 'prefetchQuery');
    const { result } = renderHook(() => useRoutePrefetch(routes.adminTemplates.id), { wrapper });

    act(() => {
      result.current.onMouseEnter();
    });
    act(() => {
      vi.advanceTimersByTime(100);
    });
    act(() => {
      result.current.onMouseLeave();
    });
    act(() => {
      vi.advanceTimersByTime(5000);
    });

    expect(spy).not.toHaveBeenCalled();
  });

  it('focus так само запускає прогрів; blur так само скасовує його', async () => {
    const spy = vi.spyOn(client, 'prefetchQuery');
    const { result } = renderHook(() => useRoutePrefetch(routes.adminRegistries.id), { wrapper });

    act(() => {
      result.current.onFocus();
    });
    act(() => {
      result.current.onBlur();
    });
    act(() => {
      vi.advanceTimersByTime(1000);
    });
    expect(spy).not.toHaveBeenCalled();

    act(() => {
      result.current.onFocus();
    });
    act(() => {
      vi.advanceTimersByTime(150);
    });
    expect(spy).toHaveBeenCalledTimes(1);
    expect(spy.mock.calls[0]?.[0]).toMatchObject({ queryKey: queryKeys.registries.list() });

    await import('@/pages/admin/RegistriesPage');
  });

  it('другий hover ТОГО САМОГО маршруту не прогріває вдруге (once per route per lifetime)', async () => {
    const spy = vi.spyOn(client, 'prefetchQuery');
    const { result } = renderHook(() => useRoutePrefetch(routes.adminMethodologies.id), { wrapper });

    act(() => {
      result.current.onMouseEnter();
      vi.advanceTimersByTime(150);
    });
    act(() => {
      result.current.onMouseLeave();
    });
    act(() => {
      result.current.onMouseEnter();
      vi.advanceTimersByTime(150);
    });

    expect(spy).toHaveBeenCalledTimes(1);

    await import('@/pages/admin/MethodologiesPage');
  });

  it('без прив’язаного маршруту (undefined) ніколи нічого не прогріває', () => {
    const spy = vi.spyOn(client, 'prefetchQuery');
    const { result } = renderHook(() => useRoutePrefetch(undefined), { wrapper });

    act(() => {
      result.current.onMouseEnter();
    });
    act(() => {
      vi.advanceTimersByTime(1000);
    });

    expect(spy).not.toHaveBeenCalled();
  });

  it('маршрут БЕЗ фабричного ключа (наприклад, "units") прогріває лише чанк — жодного виклику prefetchQuery', async () => {
    const spy = vi.spyOn(client, 'prefetchQuery');
    const { result } = renderHook(() => useRoutePrefetch(routes.adminUnits.id), { wrapper });

    act(() => {
      result.current.onMouseEnter();
    });
    act(() => {
      vi.advanceTimersByTime(150);
    });

    expect(spy).not.toHaveBeenCalled();

    await import('@/pages/admin/UnitsPage');
  });
});

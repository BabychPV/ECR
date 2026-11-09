import { describe, it, expect, vi, afterEach } from 'vitest';
import type { JSX, ReactNode } from 'react';
import { renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { PeriodCalendarDto } from '@/api/types';
import { useCalendarsOf } from '@/features/projects/projectCalendars';

/**
 * ✎ g1 (приймання 09.11, P3): рамка смуги переліку документів має стояти на ПОТОЧНОМУ ПЕРІОДІ проєкту (`isCurrent`,
 * D-77) — тому самому, що в шапці проєкту. Шапка, Periods і Settings читають календар за ключем `['periods', id]`;
 * «Pin current period…» інвалідує й перечитує саме його.
 *
 * ⛔ Доказ (шлях людини): перелік документів → Settings проєкту → «Pin current period… November» → назад у перелік
 * (у межах хвилини). До фіксу обгортка переліку лежала під власним ключем зі `staleTime` 60 с і обводила ЖОВТЕНЬ,
 * хоча шапка вже казала «current period November 2026».
 */

function calendar(current: number): unknown {
  return {
    projectId: 7,
    periodKind: 'Monthly',
    currentPeriodMode: 'Pinned',
    timeZoneId: 'UTC',
    periods: [10, 11].map((sequence) => ({
      id: sequence,
      periodKey: 202600 + sequence,
      year: 2026,
      sequence,
      state: 'Open',
      startsAt: '2026-10-01T00:00:00Z',
      endsAt: '2099-01-01T00:00:00Z',
      graceEndsAt: '2099-01-01T00:00:00Z',
      reopenedUntil: null,
      isCurrent: 202600 + sequence === current,
    })),
  };
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

describe('useCalendarsOf: поточний період — той самий, що в шапці проєкту (g1)', () => {
  it('після «Pin current period» на сторінці проєкту перелік документів обводить новий поточний період', async () => {
    let current = 202610;
    vi.stubGlobal(
      'fetch',
      vi.fn(async () =>
        new Response(JSON.stringify(calendar(current)), { status: 200, headers: { 'Content-Type': 'application/json' } }),
      ),
    );

    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const wrapper = ({ children }: { children: ReactNode }): JSX.Element => (
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    );
    const currentOf = (calendars: readonly (PeriodCalendarDto | undefined)[]): number | undefined =>
      calendars[0]?.periods.find((period) => period.isCurrent)?.periodKey;

    // 1. Перелік документів: рамка — жовтень.
    const list = renderHook(() => useCalendarsOf([7]), { wrapper });
    await waitFor(() => expect(currentOf(list.result.current)).toBe(202610));
    list.unmount();

    // 2. Settings проєкту: «Pin current period… November» → CurrentPeriodSetting інвалідує, і сторінка перечитує
    //    `['periods', 7]` (шапка «current period November 2026»).
    await new Promise((resolve) => setTimeout(resolve, 5));
    current = 202611;
    await client.fetchQuery({
      queryKey: ['periods', 7],
      queryFn: () => apiFetch<PeriodCalendarDto>('/api/v1/projects/7/periods'),
    });

    // 3. Назад у перелік (менше ніж за хвилину): рамка — листопад, як у шапці.
    const again = renderHook(() => useCalendarsOf([7]), { wrapper });
    expect(currentOf(again.result.current)).toBe(202611);
  });
});

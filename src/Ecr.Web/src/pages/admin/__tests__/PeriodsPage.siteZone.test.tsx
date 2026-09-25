import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { PeriodsPage, periodCaption } from '@/pages/admin/PeriodsPage';
import { testTheme } from '@/test/render';

/**
 * `X-34`/`F-20`: дати періодів на `/admin/periods` показувалися в поясі
 * БРАУЗЕРА: 202601 проєкту на `Asia/Aqtau` — як «Dec 31, 2025 — Mar 16, 2026»,
 * «Grace until: Feb 14, 9:00 PM» при справжньому 15.02 00:00 +05, а квартал
 * 202504 — «April 2025».
 *
 * ⚠ Пояс проєкту в тесті — `Pacific/Kiritimati` (+14:00): північ там — це
 * ПОПЕРЕДНЯ доба в будь-якому поясі машини, де ганяються тести (UTC, Київ,
 * Актау). Тобто мутація «форматувати без `timeZone`» дає інший день у КОЖНОМУ
 * середовищі, а не лише в «зручному».
 */

const Zone = 'Pacific/Kiritimati';

function calendarOf(periodKind: 'Monthly' | 'Quarterly') {
  return {
    projectId: 7,
    periodKind,
    currentPeriodMode: 'Auto' as const,
    timeZoneId: Zone,
    policy: { id: 1, code: 'ECR-Standard', openOffsetDays: 0, graceOffsetDays: 15, hardCloseOffsetDays: 45, yearGraceOffsetDays: 45 },
    periods: [
      {
        id: 1,
        periodKey: periodKind === 'Monthly' ? 202601 : 202504,
        year: periodKind === 'Monthly' ? 2026 : 2025,
        sequence: periodKind === 'Monthly' ? 1 : 4,
        // Моменти майданчика, як їх віддає сервер (`ToSite`, зі зсувом).
        startsAt: '2026-01-01T00:00:00+14:00',
        endsAt: '2026-03-17T00:00:00+14:00',
        state: 'Open',
        graceEndsAt: '2026-02-15T00:00:00+14:00',
        reopenedUntil: null,
        isCurrent: false,
      },
    ],
  };
}

function mockFetch(periodKind: 'Monthly' | 'Quarterly'): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      const json = (body: unknown): Response =>
        new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

      if (url.includes('/periods')) return json(calendarOf(periodKind));
      if (url.includes('/api/v1/projects')) {
        return json({
          items: [{ id: 7, code: 'PRJ-7', status: 'Active', periodKind, periodCount: 1, currentPeriodId: null, timeZoneId: Zone }],
          nextCursor: null,
          totalCount: 1,
        });
      }

      return json(null);
    }),
  );
}

function show(): void {
  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={['/admin/periods?projectId=7']}>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <PeriodsPage />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

/** Текст без нерозривних пробілів ICU (`Intl` ставить U+202F перед AM/PM). */
const norm = (text: string | null | undefined): string => (text ?? '').replace(/[  ]/g, ' ');

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('PeriodsPage: межі періодів — у поясі майданчика і підписані чесно', () => {
  it('початок, останній день прийому даних і пільговий строк — у поясі проєкту', { timeout: 60_000 }, async () => {
    mockFetch('Monthly');
    show();

    const times = await screen.findAllByText((_, el) => el?.tagName === 'TIME', {}, { timeout: 20_000 });
    const shown = times.map((el) => norm(el.textContent));

    // ⛔ Мутація «форматувати в поясі браузера» (старий `<Timestamp>`) дає
    // «Dec 31, 2025» — день раніше в будь-якому поясі машини.
    expect(shown[0]).toBe('Jan 1, 2026');

    // ⛔ `endsAt` — ВИКЛЮЧНЕ закриття (північ 17.03): останній день, коли дані
    // ще приймаються, — 16.03. Мутація «без `inclusiveEnd`» дає «Mar 17, 2026».
    expect(shown[1]).toBe('Mar 16, 2026');

    // Пільговий строк — із годиною, у поясі майданчика: 15.02 00:00, а не
    // «Feb 14, 9:00 PM» пояса браузера.
    expect(shown[2]).toBe('Feb 15, 2026, 12:00 AM');

    // Точний момент сервера — у розмітці, без змін.
    expect(times[0]?.getAttribute('dateTime')).toBe('2026-01-01T00:00:00+14:00');
  });

  it('квартальний проєкт: 202504 — четвертий квартал 2025, а не «April 2025»', { timeout: 60_000 }, async () => {
    mockFetch('Quarterly');
    show();

    const caption = await screen.findByText(
      (_, el) => el?.hasAttribute('data-period-caption') === true,
      {},
      { timeout: 20_000 },
    );

    expect(caption.textContent).toBe('⟦periods.quarterOf (quarter=4, year=2025)⟧');
    expect(caption.textContent).not.toContain('April');
  });

  it('місячний підпис — назва місяця, незалежно від поясу машини', () => {
    expect(periodCaption(2026, 1, 'Monthly')).toBe('January 2026');
    expect(periodCaption(2026, 12, 'Monthly')).toBe('December 2026');
    expect(periodCaption(2026, 1, 'Yearly')).toBe('2026');
  });
});

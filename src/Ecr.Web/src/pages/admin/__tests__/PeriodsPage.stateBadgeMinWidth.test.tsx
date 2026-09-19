import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { PeriodsPage } from '@/pages/admin/PeriodsPage';
import { testTheme } from '@/test/render';

/**
 * UI-аудит, lane 8: `lane8-period-state-badge-truncated.md` — попередній
 * фікс (Аудит-пас 5, `PeriodsPage.badgeOverflow.test.tsx`) обгорнув таблицю
 * в `ScrollArea`, але цього виявилось НЕДОСТАТНЬО: жива перевірка в
 * браузері (~554px, типова ширина Browser pane) показала бейдж стану як
 * нечитабельне «S…» замість «SCHEDULED».
 *
 * Корінь — `table-layout: auto` бере ширину стовпця з того, що фактично
 * РЕНДЕРИТЬСЯ; `.mantine-Badge-label`'s власний `overflow: hidden` дозволяє
 * йому «поміститись» у будь-яку ширину, тож таблиця лишається у 100% ширини
 * контейнера замість перемикання `ScrollArea` на горизонтальну прокрутку.
 * `miw="fit-content"` на самому `Badge` тримає його мінімальну ширину рівно
 * рівною тексту — підтверджено живим виміром у браузері: без цього стилю
 * `label`'s `clientWidth` (9px) багаторазово менший за `scrollWidth` (65px
 * для «Scheduled»); зі стилем обидва збігаються.
 *
 * ⛔ Мутаційний доказ структурний (jsdom не рахує layout): перевіряється, що
 * САМЕ той інлайн-стиль, який лікує дефект (`min-width: fit-content`),
 * застосований РІВНО на бейджі стану.
 *
 * ✎ 2026-09-19. Твердження НЕ змінилося — змінилося, ХТО його виконує. Стиль
 * стояв пропом `miw="fit-content"` на `Badge` цієї сторінки; тепер стан малює
 * `StatusBadge`, і мінімальна ширина стала його безумовною властивістю (той
 * самий дефект довелося ловити окремо ще й для `SnapshotsPage`, тобто наступна
 * таблиця забула б цей проп утретє). Що набір її тримає для ВСІХ різновидів і
 * в обох варіантах заливки, доводить `shared/ui/__tests__/StatusBadge.test.tsx`;
 * цей файл лишається доказом, що вона доходить до живої сторінки.
 *
 * ⚠ Локатор переведено з `findByText('Scheduled')` на `data-status-state`:
 * підпис бейджа тепер приходить із каталогу (`status.period.Scheduled` — у
 * сіді це «Not open yet»), а не є кодом сервера. Пошук за кодом шукав би текст,
 * якого на екрані більше немає.
 */
const project = {
  id: 7,
  code: 'PRJ-7',
  status: 'Active' as const,
  periodKind: 'Monthly' as const,
  periodCount: 1,
  currentPeriodId: null,
  timeZoneId: 'Europe/Kyiv',
};

const calendar = {
  projectId: 7,
  periodKind: 'Monthly' as const,
  currentPeriodMode: 'Auto' as const,
  timeZoneId: 'Europe/Kyiv',
  // Q-337, lane 2: `PeriodCalendarDto.Policy` — тултипи заголовків «Range»/
  // «Grace until» тепер підставляють ці числа.
  policy: { id: 1, code: 'ECR-Standard', openOffsetDays: 0, graceOffsetDays: 15, hardCloseOffsetDays: 45, yearGraceOffsetDays: 45 },
  periods: [
    {
      id: 1,
      periodKey: 202601,
      sequence: 1,
      startsAt: '2026-01-01',
      endsAt: '2026-02-01',
      state: 'Scheduled',
      graceEndsAt: null,
      reopenedUntil: null,
      isCurrent: false,
    },
  ],
};

function mockFetch(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/api/v1/me')) {
        return new Response(
          JSON.stringify({
            denies: [],
            grants: {},
            isSimulation: false,
            language: 'en',
            mustChangePassword: false,
            permissions: ['Project.Manage'],
            simulatedForUserId: null,
            userId: 1,
            userName: 'tester',
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }

      if (url.includes('/api/v1/projects') && url.includes('/periods')) {
        return new Response(JSON.stringify(calendar), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      if (url.includes('/api/v1/projects')) {
        return new Response(JSON.stringify({ items: [project], nextCursor: null, totalCount: 1 }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      return new Response(JSON.stringify(null), { status: 200 });
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={['/admin/periods?projectId=7']}>
        <QueryClientProvider client={client}>
          <PeriodsPage />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

const SlowEnvTimeout = 400_000;

describe('PeriodsPage: бейдж стану не стискається нижче власного тексту (lane8)', () => {
  it(
    'бейдж «Scheduled» має min-width: fit-content',
    async () => {
      mockFetch();
      show();

      // ⚠ Рядок періоду в таблиці, а не сам бейдж: чекаємо на розмальований
      // перелік, і лише тоді беремо корінь бейджа за станом.
      await screen.findByText('202601', {}, { timeout: SlowEnvTimeout });

      const badgeRoot = document.querySelector<HTMLElement>('[data-status-state="Scheduled"]');

      expect(badgeRoot).not.toBeNull();
      expect(badgeRoot?.classList.contains('mantine-Badge-root')).toBe(true);
      // ⛔ Мутаційний доказ: без цього стилю таблиця (100% ширини контейнера)
      // стискає бейдж до нечитабельного «S…» замість перемикання ScrollArea
      // на горизонтальну прокрутку.
      expect(badgeRoot?.style.minWidth).toBe('fit-content');
    },
    SlowEnvTimeout,
  );
});

import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { PeriodsPage } from '@/pages/admin/PeriodsPage';
import { testTheme } from '@/test/render';
import { statusTable } from '@/shared/ui/StatusBadge';

/**
 * Стан періоду фарбує набір, а не сама сторінка.
 *
 * ⛔ Тут стояла власна `stateColor(state)` з `default: 'blue'`, і під цей
 * `default` потрапляв `Scheduled` — стан, який у переліку домену Є
 * (`PeriodState`). Тобто «ще не відкрито, робити нічого» малювалося тим самим
 * кольором, що й стан, про який сторінка нічого не знає: на екрані ці два
 * випадки були нерозрізненні. У наборі `Scheduled` — `muted` («стан є, але
 * робити в ньому нічого»), невідоме — `warning` («розберіться»).
 *
 * ⚠ `Grace` лишається окремим тоном, і це те саме рішення, що стояло в
 * помічнику: правка в пільговому строку позначається як пізня (`D-70`) і
 * виглядає в аудиті інакше, тож зводити її до «відкрито» не можна.
 *
 * ⚠ Тон читається з розмітки (`data-status-tone`), а не з кольору CSS.
 * Контраст самих тонів стереже `shared/ui/__tests__/statusBadgeContrast.test.ts`.
 */
const project = {
  id: 7,
  code: 'PRJ-7',
  status: 'Active' as const,
  periodKind: 'Monthly' as const,
  periodCount: 5,
  currentPeriodId: null,
  timeZoneId: 'Europe/Kyiv',
};

function periodWith(state: string, n: number) {
  return {
    id: n,
    periodKey: 202600 + n,
    sequence: n,
    startsAt: '2026-01-01',
    endsAt: '2026-02-01',
    state,
    graceEndsAt: null,
    reopenedUntil: null,
    isCurrent: false,
  };
}

/*
 * ⚠ ВСІ чотири стани `PeriodState` одночасно, плюс п'ятий — невідомий. Тест на
 * одному `Open` лишався б зеленим і тоді, коли всі стани стали однаковими:
 * доказ — саме розрізнення.
 */
const states = ['Scheduled', 'Open', 'Grace', 'Closed', 'Frozen'];

const calendar = {
  projectId: 7,
  periodKind: 'Monthly' as const,
  currentPeriodMode: 'Auto' as const,
  timeZoneId: 'Europe/Kyiv',
  policy: {
    id: 1,
    code: 'ECR-Standard',
    openOffsetDays: 0,
    graceOffsetDays: 15,
    hardCloseOffsetDays: 45,
    yearGraceOffsetDays: 45,
  },
  periods: states.map((state, index) => periodWith(state, index + 1)),
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

function badgeOf(state: string): Element | null {
  return document.querySelector(`[data-status-state="${state}"]`);
}

function toneOf(state: string): string | null {
  return badgeOf(state)?.getAttribute('data-status-tone') ?? null;
}

/** ⚠ Та сама межа, що в решті тестів цієї сторінки: вона важка в jsdom. */
const SlowEnvTimeout = 400_000;

async function ready(): Promise<void> {
  await screen.findByText('202605', {}, { timeout: SlowEnvTimeout });
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('PeriodsPage: тон стану періоду приходить із набору', () => {
  it(
    'чотири стани домену — і вони РІЗНІ, а не один колір на всіх',
    async () => {
      mockFetch();
      show();
      await ready();

      // ⛔ Мутаційний доказ (RED, якщо повернути `stateColor`): локальний
      // помічник не лишає в розмітці ані `data-status-tone`, ані
      // `data-status-state`, тож кожен рядок нижче стає `expected null to be …`.
      expect(toneOf('Grace')).toBe('warning');
      expect(toneOf('Scheduled')).toBe('muted');
      expect(toneOf('Open')).toBe('neutral');
      expect(toneOf('Closed')).toBe('neutral');
    },
    SlowEnvTimeout,
  );

  it(
    '«ще не відкрито» відрізняється від стану, якого набір не знає',
    async () => {
      /*
       * ⛔ Головне твердження цього файлу. `Scheduled` — повноцінний член
       * `PeriodState`, і старий `default: 'blue'` звів його з невідомим станом
       * в один колір.
       */
      expect(statusTable.period['Frozen'], 'фікстура має бути СПРАВДІ невідомим станом').toBeUndefined();
      expect(statusTable.period['Scheduled'], '`Scheduled` — відомий стан домену').toBeDefined();

      mockFetch();
      show();
      await ready();

      expect(toneOf('Frozen')).toBe('warning');
      expect(toneOf('Scheduled')).not.toBe(toneOf('Frozen'));

      expect(badgeOf('Frozen')?.getAttribute('data-status-known')).toBe('false');
      expect(badgeOf('Scheduled')?.getAttribute('data-status-known')).toBe('true');
    },
    SlowEnvTimeout,
  );

  it(
    'підпис бере рядок каталогу, а не друкує код сервера',
    async () => {
      mockFetch();
      show();
      await ready();

      /*
       * ⚠ Цей файл каталогу не завантажує, тож `t()` віддає позначений ключ
       * (`D-138`) — і саме ключ є доказом, що підпис пройшов через `t()`, а не
       * через `{period.state}`. У сіді `status.period.Scheduled` — це
       * «Not open yet», а не «Scheduled»: підпис на екрані змінюється, і це
       * навмисно (код сервера не є текстом інтерфейсу).
       */
      expect(badgeOf('Scheduled')?.textContent).toBe('⟦status.period.Scheduled⟧');
      expect(badgeOf('Scheduled')?.textContent).not.toBe('Scheduled');
    },
    SlowEnvTimeout,
  );
});

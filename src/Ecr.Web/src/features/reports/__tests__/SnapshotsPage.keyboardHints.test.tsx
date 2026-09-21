import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SnapshotsPage } from '@/pages/admin/SnapshotsPage';
import { testTheme } from '@/test/render';

/**
 * Пояснювальні підказки переліку зрізів доступні з клавіатури й для
 * екранного читача.
 *
 * ⛔ До цього обидві підказки були атрибутом `title`: позначка формату
 * (`snapshots.formatLegacyHint`) не отримувала фокуса зовсім, а межу Excel
 * біля «Export» (`snapshots.exportHint`) браузер показує лише під мишею —
 * з клавіатури її не видно ніколи, а екранні читачі читають `title`
 * непослідовно.
 *
 * ⚠ Тест іде ТАБУЛЯЦІЄЮ, а не `element.focus()`: `focus()` фокусує будь-що з
 * `tabIndex=-1` і довів би лише те, що елемент МОЖНА сфокусувати скриптом.
 * Опис перевіряє фільтр `description` у `getByRole` — accname через
 * `dom-accessibility-api`, ту саму реалізацію, що стоїть за
 * `toHaveAccessibleDescription` у `jest-dom`, якого проєкт не підключає.
 */

const project = { id: 42, code: 'KASH_2026', status: 'Active' as const };

const snapshot = {
  id: 7,
  builtAt: '2026-01-15T10:00:00Z',
  contentHash: 'abc',
  isCurrent: true,
  periodKey: 202601,
  projectId: 42,
  reportVersionId: 1,
  rowCount: 10,
  status: 'Approved',
  hashFormat: 'legacy',
};

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
}

function mockFetch(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/api/v1/me')) {
        return json({
          denies: [],
          grants: {},
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions: ['Report.ViewRegulatory', 'Report.Export'],
          simulatedForUserId: null,
          userId: 1,
          userName: 'tester',
        });
      }

      if (url.includes('/api/v1/reports/snapshots')) return json([snapshot]);
      if (url.includes('/api/v1/reports')) return json([]);

      if (url.includes('/api/v1/projects')) {
        return json({ items: [project], nextCursor: null, totalCount: 1 });
      }

      return json(null);
    }),
  );
}

const SlowEnvTimeout = 400_000;

async function show(): Promise<void> {
  mockFetch();

  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={['/admin/snapshots']}>
        <QueryClientProvider client={client}>
          <SnapshotsPage />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );

  // Таблиця домальована: контрольна сума зрізу на екрані.
  await screen.findByText('abc', {}, { timeout: SlowEnvTimeout });
}

/** Табуляція до `target`; `false` — клавіатура до нього не дійшла. */
async function tabTo(target: HTMLElement): Promise<boolean> {
  const user = userEvent.setup();

  for (let step = 0; step < 80; step++) {
    await user.tab();
    if (document.activeElement === target) return true;
  }

  return false;
}

/**
 * Видима підказка з цим текстом.
 *
 * ⚠ Не «перша-ліпша `tooltip`»: табуляція проходить повз інші підказки рядка
 * («Export» стоїть раніше за позначку), і попередня ще згасає, коли фокус
 * уже на наступному елементі.
 */
async function tooltipSays(text: string): Promise<void> {
  await waitFor(
    () => {
      const shown = screen.queryAllByRole('tooltip').map((tip) => tip.textContent ?? '');
      expect(shown.some((content) => content.includes(text)), `видимі підказки: ${shown.join(' | ')}`).toBe(true);
    },
    { timeout: 5_000 },
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('SnapshotsPage: підказки доступні з клавіатури', () => {
  it(
    'позначка формату: табуляція доходить до неї, опис читається, підказка видна',
    async () => {
      await show();

      const badge = document.querySelector<HTMLElement>('[data-hash-format="legacy"]');
      expect(badge, 'позначки legacy немає').not.toBeNull();

      expect(await tabTo(badge as HTMLElement), 'позначка не отримує фокуса з клавіатури').toBe(true);

      // Екранний читач: опис прив'язаний до самого елемента, без наведення.
      expect(screen.queryAllByRole('generic', { description: '⟦snapshots.formatLegacyHint⟧' })).toContain(badge);

      // Зрячий користувач клавіатури: підказка з'являється на фокусі.
      await tooltipSays('⟦snapshots.formatLegacyHint⟧');
    },
    SlowEnvTimeout,
  );

  it(
    '«Export»: лишається посиланням, межа Excel — його опис і видна на фокусі',
    async () => {
      await show();

      const link = screen.getByRole('link', { name: /snapshots\.export⟧/ });

      expect(await tabTo(link), 'посилання не отримує фокуса з клавіатури').toBe(true);
      expect(link.getAttribute('href')).toBe('/api/v1/reports/snapshots/7/export.xlsx');

      expect(screen.queryAllByRole('link', { description: '⟦snapshots.exportHint⟧' })).toContain(link);

      await tooltipSays('⟦snapshots.exportHint⟧');
    },
    SlowEnvTimeout,
  );
});

import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { JobsPage } from '@/pages/admin/JobsPage';
import { formatDateTime } from '@/shared/format';

/**
 * `UI-07`: момент старту задачі читабельний на екрані й ТОЧНИЙ у розмітці.
 *
 * ⛔ Тверджень обов'язково ДВА, і жодне з них поодинці нічого не доводить:
 *
 *   1. видимий текст НЕ дорівнює сирому входу. Без цього рівність із
 *      `formatDateTime(...)` лишилася б зеленою й на компоненті, який нічого
 *      не форматує (`formatDateTime` сирого ISO не повертає — але це треба
 *      сказати тестом, а не вірою);
 *   2. `dateTime` дорівнює РІВНО тому рядку, що віддав сервер. Це та половина,
 *      заради якої знято заперечення з коментаря в `JobsPage`: перелік задач
 *      звіряють із журналом аудиту, і момент мусить лишатися однозначним.
 *
 * ⚠ Очікуваний текст береться з `formatDateTime(...)` того самого модуля, а не
 * пишеться дослівно («Sep 19, 2026, 9:58 AM»). Дослівний рядок був би тестом
 * версії ICU у Node і мови набору: він почервонів би від оновлення Node,
 * нічого не зламавши.
 */
const Started = '2026-09-19T09:58:00Z';

const jobs = [
  {
    jobCode: 'Ecr.Application.Ports.IRecalculationJob',
    jobId: 'IRecalculationJob#42',
    percent: 40,
    startedAt: Started,
    state: 'Running',
    updatedAt: '2026-09-19T10:00:00Z',
  },
];

function mockFetch(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/api/v1/jobs')) {
        return new Response(JSON.stringify(jobs), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      return new Response(JSON.stringify(null), { status: 404 });
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <MemoryRouter initialEntries={['/admin/jobs']}>
        <QueryClientProvider client={client}>
          <JobsPage />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

/** Чекає момент старту в розмітці й віддає сам елемент. */
async function moment(): Promise<HTMLTimeElement> {
  return await waitFor(() => {
    const node = document.querySelector('time');

    expect(node, 'момент старту не намальовано елементом <time>').not.toBeNull();

    return node as HTMLTimeElement;
  });
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('JobsPage: момент старту — читабельний на екрані, точний у розмітці', () => {
  it('показує момент мовою набору, а не сирий рядок сервера', async () => {
    mockFetch();
    show();

    const node = await moment();

    // ⛔ Половина перша: користувач більше не бачить сирого ISO.
    expect(node.textContent).not.toBe(Started);
    expect(node.textContent).not.toMatch(/T\d{2}:\d{2}/);
    expect(screen.queryByText(Started), 'сирий рядок лишився видимим текстом').toBeNull();

    expect(node.textContent).toBe(formatDateTime(Started));
  });

  it('точне значення лишилося в розмітці — доказ для звірки з журналом', async () => {
    mockFetch();
    show();

    const node = await moment();

    // ⛔ Половина друга: РІВНО те, що віддав сервер, а не «схоже на нього».
    expect(node.getAttribute('datetime')).toBe(Started);
    expect(node.getAttribute('title')).toBe(Started);
  });
});

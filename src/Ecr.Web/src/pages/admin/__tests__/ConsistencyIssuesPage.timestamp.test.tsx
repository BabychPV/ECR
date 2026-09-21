import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ConsistencyIssuesPage } from '@/pages/admin/ConsistencyIssuesPage';
import { formatDateTime } from '@/shared/format';

/**
 * `UI-07`: момент знахідки читабельний на екрані й ТОЧНИЙ у розмітці.
 *
 * ⛔ Обидві половини обов'язкові — див. `JobsPage.timestamp.test.tsx`. Тут
 * друга половина — робочий інструмент: саме за моментом знахідку знаходять у
 * `aud.ConsistencyIssue` запитом до бази, коли треба подивитися ширше, ніж
 * показує екран.
 *
 * ⚠ Очікуваний текст — з `formatDateTime(...)`, а не дослівний рядок.
 */
const Detected = '2026-09-18T03:00:00Z';

const Finding = {
  id: 7,
  detectedAt: Detected,
  severity: 3,
  ruleCode: 'BROKEN_FK',
  entityType: 'doc.TableRow',
  entityId: 4021,
  message: 'Рядок 4021 посилається на екземпляр таблиці 77 періоду 202601, якого не існує.',
  resolvedAt: null,
  resolvedByUserId: null,
};

function mockFetch(): void {
  vi.stubGlobal(
    'fetch',
    // ⚠ Профіль (`/api/v1/me`) — окрема відповідь: екран питає права для дії
    // «перевірити зараз», і сторінка знахідок на місці профілю — не профіль.
    vi.fn(
      async (input: RequestInfo | URL) =>
        new Response(
          JSON.stringify(
            String(input).endsWith('/api/v1/me')
              ? { permissions: [] }
              : { items: [Finding], nextCursor: null, totalCount: null },
          ),
          {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          },
        ),
    ),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <MemoryRouter initialEntries={['/admin/consistency']}>
        <QueryClientProvider client={client}>
          <ConsistencyIssuesPage />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

/** Чекає момент знахідки в розмітці й віддає сам елемент. */
async function moment(): Promise<HTMLTimeElement> {
  return await waitFor(() => {
    const node = document.querySelector('time');

    expect(node, 'момент знахідки не намальовано елементом <time>').not.toBeNull();

    return node as HTMLTimeElement;
  });
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('ConsistencyIssuesPage: момент знахідки — читабельний на екрані, точний у розмітці', () => {
  it('показує момент мовою набору, а не сирий рядок сервера', async () => {
    mockFetch();
    show();

    const node = await moment();

    expect(node.textContent).not.toBe(Detected);
    expect(node.textContent).not.toMatch(/T\d{2}:\d{2}/);
    expect(screen.queryByText(Detected), 'сирий рядок лишився видимим текстом').toBeNull();

    expect(node.textContent).toBe(formatDateTime(Detected));
  });

  it('точне значення лишилося в розмітці — за ним знахідку шукають у базі', async () => {
    mockFetch();
    show();

    const node = await moment();

    expect(node.getAttribute('datetime')).toBe(Detected);
    expect(node.getAttribute('title')).toBe(Detected);
  });
});

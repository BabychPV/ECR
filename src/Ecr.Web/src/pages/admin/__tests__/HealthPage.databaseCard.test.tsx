import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { HealthPage } from '@/pages/admin/HealthPage';
import { formatNumber } from '@/shared/format/number';

/**
 * Стенд, ручний прохід: картка «Database» показувала сирі ключі
 * `rawDataPointRows 0` / `rawDataPointWarnRows 20000000`, а `limitations` —
 * повні речення, зліплені через кому в один рядок.
 */
const limitations = [
  'Index maintenance runs in a short window (ONLINE = ON is not available).',
  'Background jobs share the primary filegroup.',
];

const report = {
  status: 'Healthy',
  totalDurationMs: 1.5,
  checks: [
    {
      name: 'db',
      status: 'Healthy',
      description: 'ok',
      durationMs: 1.1,
      data: {
        edition: 'Standard',
        filegroups: ['PRIMARY', 'FG_FACTS_2026'],
        limitations,
        rawDataPointRows: 0,
        rawDataPointWarnRows: 20000000,
      },
    },
  ],
};

function show(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(() =>
      Promise.resolve(
        new Response(JSON.stringify(report), { status: 200, headers: { 'Content-Type': 'application/json' } }),
      ),
    ),
  );

  render(
    <MantineProvider>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <HealthPage />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

async function itemOf(labelKey: string): Promise<Element> {
  const label = await screen.findByText(`⟦${labelKey}⟧`);
  const item = label.closest('[data-key-value-item]');

  expect(item).not.toBeNull();

  return item as Element;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('Панель бази: підписи лічильників і перелік обмежень', () => {
  it('rawDataPoint* мають підписи з каталогу, а не сирі ключі', async () => {
    show();

    await itemOf('health.database.rawDataPointRows');
    await itemOf('health.database.rawDataPointWarnRows');

    expect(screen.queryByText('rawDataPointRows')).toBeNull();
    expect(screen.queryByText('rawDataPointWarnRows')).toBeNull();
  });

  it('лічильники форматуються з розділювачами груп', async () => {
    show();

    expect((await itemOf('health.database.rawDataPointRows')).querySelector('dd')?.textContent).toBe(formatNumber(0));
    expect((await itemOf('health.database.rawDataPointWarnRows')).querySelector('dd')?.textContent).toBe(
      formatNumber(20000000),
    );
    expect(formatNumber(20000000)).not.toBe('20000000');
  });

  it('limitations: кожне речення в окремому рядку, без склейки через кому', async () => {
    show();

    const item = await itemOf('health.database.limitations');
    const lines = [...item.querySelectorAll('li')].map((li) => li.textContent);

    expect(lines).toEqual(limitations);
    expect(item.querySelector('dd')?.textContent).not.toContain('., ');
  });

  it('короткі переліки (файлові групи) лишаються через кому', async () => {
    show();

    const item = await itemOf('health.database.filegroups');

    expect(item.querySelector('dd')?.textContent).toBe('PRIMARY, FG_FACTS_2026');
    expect(item.querySelector('li')).toBeNull();
  });
});

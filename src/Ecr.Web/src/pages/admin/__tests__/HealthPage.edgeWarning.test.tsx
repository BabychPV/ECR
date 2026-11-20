import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { HealthPage } from '@/pages/admin/HealthPage';

/**
 * PSS-L6b: примітка «рядки поза підготовленими річними партиціями» на картці БД.
 * Це довідка (info), а не збій: статус перевірки лишається Healthy, а поля `edge*` не стають рядками переліку.
 */
function show(extra: Record<string, unknown>): void {
  const report = {
    status: 'Healthy',
    totalDurationMs: 1.5,
    checks: [
      {
        name: 'db',
        status: 'Healthy',
        description: 'База доступна.',
        durationMs: 1.1,
        data: { edition: 'Developer', partitionsAhead: 15, ...extra },
      },
    ],
  };

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

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('Картка бази: попередження про крайні партиції', () => {
  it('edgeWarning=true: показує інформаційну примітку без рядків edge* у переліку', async () => {
    show({ edgeWarning: true, edgeRowsFirst: 7, edgeRowsLast: 0 });

    const note = await screen.findByTestId('health-db-edge-warning');

    expect(note.textContent).toContain('⟦health.db.edgeWarning.title⟧');
    expect(note.getAttribute('role')).toBe('status');
    expect(screen.queryByText('edgeWarning')).toBeNull();
    expect(screen.queryByText('edgeRowsFirst')).toBeNull();
  });

  it('edgeWarning=false або поля немає: примітки немає, перелік на місці', async () => {
    show({ edgeWarning: false });
    await screen.findByText('⟦health.database.partitionsAhead⟧');

    expect(screen.queryByTestId('health-db-edge-warning')).toBeNull();
  });
});

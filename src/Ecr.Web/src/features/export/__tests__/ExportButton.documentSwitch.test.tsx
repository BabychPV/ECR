import type { ReactNode } from 'react';
import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { notifications } from '@mantine/notifications';
import { ExportButton } from '../ExportButton';
import { testTheme } from '@/test/render';

/**
 * AN-39 / L8-19: експорт не був прив'язаний до документа/періоду, для яких його запущено.
 * Перехід на інший документ (кнопка лишається змонтованою) давав посилання з НОВИМ id на
 * книгу старого, а «Формується…» крутилось на чужому документі.
 */
vi.mock('@mantine/notifications', () => ({ notifications: { show: vi.fn() } }));

let jobState = 'Running';

function mockFetch(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/export') && !url.includes('/jobs/')) {
        return new Response(JSON.stringify({ jobId: 'job-1' }), {
          status: 202,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      if (url.includes('/jobs/job-1')) {
        return new Response(
          JSON.stringify({ jobId: 'job-1', state: jobState, percent: 50, message: 'export-key-abc', error: null }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }

      throw new Error(`неочікуваний запит у тесті: ${url}`);
    }),
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.mocked(notifications.show).mockClear();
  jobState = 'Running';
});

describe('ExportButton: прив’язка до документа (L8-19)', () => {
  it('після переходу на інший документ посилання веде на книгу ТОГО документа, а кнопка не «Формується»', async () => {
    mockFetch();
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const tree = (documentId: number): ReactNode => (
      <MantineProvider theme={testTheme}>
        <QueryClientProvider client={client}>
          <ExportButton documentId={documentId} periodKey={202601} language="en" />
        </QueryClientProvider>
      </MantineProvider>
    );

    const user = userEvent.setup();
    const view = render(tree(1));

    await user.click(screen.getByRole('button'));
    await waitFor(() => expect(vi.mocked(fetch).mock.calls.some((call) => String(call[0]).includes('/jobs/job-1'))).toBe(true));

    // Перехід на документ 2, поки задача документа 1 ще будується.
    view.rerender(tree(2));

    // Кнопка нового документа - не в стані «формується» чужого експорту.
    expect(screen.getByRole('button').getAttribute('data-export-state')).toBe('idle');

    jobState = 'Succeeded';
    await waitFor(() => expect(notifications.show).toHaveBeenCalled(), { timeout: 8000 });

    const call = vi.mocked(notifications.show).mock.calls[0]?.[0] as { message: ReactNode };
    view.unmount();
    render(<MantineProvider theme={testTheme}>{call.message}</MantineProvider>);

    const href = screen.getByRole('link').getAttribute('href') ?? '';
    expect(href).toContain('/api/v1/documents/1/export/');
    expect(href).not.toContain('/documents/2/');
  }, 20_000);
});
